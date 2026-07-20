# P1 Task 2 — Vector artifact contract (`docs/contracts/vectors-v1.md`)

**Status:** complete
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p1` @ `25794d0` (ahead 1 of origin/main)
**Dirty state at report time:** `?? docs/contracts/vectors-v1.md` (untracked, the only change)
**Commit SHA: none - parallel-lead-commit**

## What was created

`docs/contracts/vectors-v1.md` — the frozen `<workspace>/.miller/vectors.db` artifact contract, with the
required sections in order: File placement and activation, `## Generation identity`,
`## Invalidation matrix`, `## Cursors`, `## Storage schema`, `## Shadow generations and rollback`,
`## Writer discipline` (incl. Corruption recovery), `## Status vocabulary`, `## Conformance`.

Voice/framing follows `docs/contracts/canary-telemetry-v1.md`: `Status: **frozen**`, "Frozen means frozen"
paragraph, explicit v2 rule, and the pre-ship amendment convention ("no amendments have been made; the
exception is spent once the first artifact is written").

## Miller calls made (orientation, required)

| Call | Result |
|---|---|
| `inspect target=SymbolSearchSidecar depth=overview` | CONFIRMED `src/Miller.Indexing/SymbolSearchSidecar.cs:12`, `public sealed class`. Confirmed the members the contract mirrors: `EnvVar` const, `Disabled` singleton, `TryOpen` (non-throwing probe), `OpenRequired` (fail-visible), `EnsureCurrent` (lock-holding writer). Doc comment wording reused for the writer-path mirror paragraph. |
| `inspect target=FullRebuildPromotion depth=overview` | CONFIRMED `src/Miller.Indexing/FullRebuildPromotion.cs:69`, `public static class`. Confirmed members `RebuildDbPathFor`, `PrepareRebuildTarget` (deletes stale `.rebuild`/`-wal`/`-shm` trio), `Promote`, `FoldWalIfPresent`. Doc comment supplied the build-beside/atomic-move/self-containment/pooling-disabled facts cited in Shadow generations. |
| `inspect target=SidecarCorruptionRecovery depth=summary` | CONFIRMED the class EXISTS at `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs:7` — but it is `internal static`, not public. Real name matches the plan; no rename needed. Read the source for the corruption-shape rule (`SQLITE_CORRUPT` 11 / `SQLITE_NOTADB` 26, primary or extended, anywhere in the chain) cited in Corruption recovery. |
| `search query="revision_file_changes"` | CONFIRMED it is the durable delta log consumed via `FreshnessReader.ChangedSince` (`tests/Miller.Tests/Indexing/FreshnessReaderTests.cs:13` doc: `LatestRevision` = `SELECT MAX(revision_id) FROM extraction_revisions`, `ChangedSince` reads the v1 `revision_file_changes` delta) and that `SymbolSearchSidecar.EnsureCurrent` already converges incrementally from it (`SymbolSearchSidecarTests.cs:348`). Both cited as the convergence-source precedent. |

No symbol names were invented; every Miller symbol cited in the contract appears in the table above.

## Consistency pass (worker red/green)

| # | Check | Result |
|---|---|---|
| 1 | Invalidation matrix covers all five identity fields | PASS — one row each for `encoder_fingerprint` (shadow rebuild), `storage_schema` (shadow rebuild), `corpus_generation` (targeted re-embed), `reader_compatibility` (reader gate), `fusion_profile` (nothing). |
| 2 | `fusion_profile` explicitly never invalidates stored vectors | PASS — stated in the identity table, the matrix ("Nothing / Nothing"), and the two explicit consequences below the matrix. |
| 3 | Lane strings byte-exact | PASS — `grep -cF`: `vec0-int8-512-cosine-v1` ×4, `vec0-int8-384-cosine-v1` ×3; format rule `vec0-<element>-<dims>-<metric>-v<schema rev>` recorded. |
| 4 | `corpus_generation` starting value | PASS — `cards-v1-chunks-v1` present ×2 (value + format rule). |
| 5 | Prompt templates byte-exact vs `bench-pins.json` | PASS — both `query_instruction` strings matched with `grep -F` against the pins file values; both `document_instruction` values recorded as empty string. |
| 6 | Model sha256 byte-exact | PASS — `421a27e5…c54340` (qwen3-0.6b-f16) and `bf40c42a…0149b65` (bge-small) matched against `bench-pins.json`. |
| 7 | Knobs: pooling / EOS / normalization / MRL order | PASS — pooling `last` vs `cls`; `<\|endoftext\|>` appended for Qwen3 only (source `eval/model-bench/bench.py:28` `QWEN_EOS`); L2 always; quantization order pinned **slice → renormalize → quantize** and called a conformance failure if reordered. |
| 8 | Status vocabulary verbatim from design §5.1 | PASS — exact-substring grep of both halves of the 9-value line succeeded; per-value meaning table added below it without altering the strings. |
| 9 | Dual cursors incl. content.db precondition | PASS — symbol/chunk cursors with per-cursor `*_last_error` + `*_last_error_at`; chunk cursor gated on `content_meta.workspace_revision >= R` AND `content_meta.schema_version` (contract pins `2`, verified in `docs/contracts/content-corpus-v1.md:111`). |
| 10 | Atomic cursor-advance-with-staged-batch | PASS — embed outside the gate, re-validate identity + `artifact_id`, single short transaction containing vec0 delete + insert + mapping + cursor advance; idempotent replay via `embed_text_hash`. |
| 11 | Escalation-to-shadow trigger list complete (5 triggers) | PASS — missing delta history, ratio threshold, identity change, `artifact_id` change, oversized transaction. `artifact_id` (never revision comparison) explicitly cited to `FullRebuildPromotion`. |
| 12 | Status reports the laggier cursor | PASS — stated in Cursors and in Status vocabulary; exact revisions JSON-only. |
| 13 | DDL internally consistent with lane strings | PASS — `int8[512]` in the default-lane DDL matches `vec0-int8-512-cosine-v1`; `{element}`/`{dims}` parameterization and the `int8[384]` fallback stated; `distance_metric=cosine` matches the `cosine` component; implementations required to derive the declaration from the lane string. |
| 14 | Integer rowids + mapping tables; text-PK vec0 not used | PASS — `symbol_vector_map` / `chunk_vector_map` with `rowid_ref INTEGER PRIMARY KEY`; "text-primary-key vec0 is alpha and is not used". |
| 15 | `path`/`kind`/`is_test` as vec0 metadata columns | PASS — declared in both vec0 tables; `language` partition-key only if justified; `path` never a partition key. |
| 16 | Prefiltered manual-distance rule for glob scoping | PASS — LIKE/GLOB unsupported in vec0 metadata ⟹ resolve rowids from the mapping table then brute-force distance; oversampling documented as approximate and restricted to where prefiltering is impractical. |
| 17 | sqlite-vec pin + `vec_version()` at open | PASS — v`0.1.9` from `scripts/spike-pins.json`, four RIDs with per-RID sha256, absolute packaged path via `LoadExtension`, `vec_version()` verified at open with a stated `unavailable (…)` mismatch behavior. |
| 18 | `MILLER_SEMANTIC=off` zero-work guarantee | PASS — three-state `off \| shadow \| on` with `0` aliasing `off`; the `off` clause enumerates the forbidden work and calls out "opens the artifact just to report status" as a violation; `disabled` status derived without filesystem access. |
| 19 | Writer discipline + reader query-embedding | PASS — only the writer-lock holder embeds corpus units/mutates; any reader may embed queries against a compatible `encoder_fingerprint`, writing nothing. |
| 20 | Cross-workspace read/degrade rule | PASS — foreign convergence only from the foreign leader, cross-workspace refresh service performs no vector convergence, reads use an already-ready compatible generation, degrade reason string verbatim. |
| 21 | Shadow promote / rollback / GC lifecycle | PASS — build-beside `.rebuild`, WAL fold + stale-sidecar removal, overwrite-move promote under the writer lock, rollback = preserve-last-compatible (not restore), GC after soak with two never-conditions. |
| 22 | Per-generation corruption recovery never touching symbols.db | PASS — registered with `SidecarCorruptionRecovery`, per-generation delete+rebuild, "Recovery never touches `symbols.db`", retry-next-convergence on failure. |
| 23 | `encoder_fingerprint` composition for BOTH pinned models | PASS — canonical field-ordered pre-image with `sha256:<64 hex>` rendering, plus a filled-in per-model table for the default and fallback pins. |
| 24 | Canary contract consistency (`storage_schema` example) | NOTED, not edited — `canary-telemetry-v1.md:473` reads "Opaque lane id, e.g. `vec0-int8-256-cosine-v1`" and `:589` uses it in a JSON example. Both read as **examples**, not pins, so no discrepancy to fix. Task 4 owns the verification; flagged here for its record. |
| 25 | No files touched outside ownership | PASS — `git status --short` shows only `?? docs/contracts/vectors-v1.md`. |

**Worker ceiling:** `scripts/test.sh` (fast suite) — `Failed: 0, Passed: 3618, Skipped: 1` (24s test
duration). The wrapper's 37s wall-clock tripwire fired, which is a pre-existing branch condition unrelated
to this task: zero files under `src/` or `tests/` were touched (the only change is one new markdown file),
so no code path changed.

## Deliberate deviations recorded in the contract

1. **Fallback pins key.** The plan's Global Constraints call the fallback entry `bge-small-f32`; the actual
   key in `eval/model-bench/bench-pins.json` is **`bge-small-en-v1.5-f32`**. The contract cites the real
   key and states in a blockquote that the pins file wins as the single source for model identity. All
   other fallback values (model name, sha256, dims, pooling, instruction) are byte-exact from the pins file.
2. **sqlite-vec `int8` not yet RID-verified.** `docs/findings/2026-07-19-sqlite-vec-aot-spike.md:148`
   records that the P0 spike exercised `float[8]` only. The contract carries an explicit blockquote making
   `int8` vec0 verification on all four RIDs a **P2b implementation gate**, so no reader mistakes the lane
   choice for a proven-on-all-RIDs runtime fact.

## Concerns for the lead

- **Cross-contract dependency:** the `## Conformance` section defers the tolerance policy numbers (dims
  exact, norm ±`1e-3`, cosine ≥`0.999`) to `semantic-sidecar-protocol-v1.md` and links
  `eval/sidecar-conformance/`. Both are Batch A siblings and did not exist when this file was written — the
  links and the section names cited (`## Conformance`) must be confirmed by Task 4.
- **Meta-key naming is now frozen by this document** (`vectors_meta` keys, `symbol_vector_map` /
  `chunk_vector_map`, `rowid_ref`, `embed_text_hash`, `build_state`, `build_progress_percent`). If Task 1
  or the P2b lane wants different names, this is the pre-ship amendment window to say so.
- The Task 4 canary check is pre-answered in row 24: the canary's `vec0-int8-256-cosine-v1` reads as an
  example in both occurrences, so no canary edit appears to be needed.
