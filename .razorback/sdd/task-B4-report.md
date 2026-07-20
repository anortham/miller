# Task B4 report — Corpus builder + dual-cursor convergence

Branch `worktree-semantic-p2`, worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`.
Tree was clean at start (HEAD `b7cfc7a`); no other workers in flight.

## What was implemented

**`src/Miller.Indexing/Semantic/SymbolCardBuilder.cs` (pure).** Card text v1 exactly as the Global Constraints
pin it: `{kind} {qualified name} {signature first line} {doc excerpt ≤300} in: {container} {path}`, 1,200-char
budget, word-boundary truncation, comment-marker stripping (`///`, `//`, `/**`, `*`, `*/`, `#`, `"""`, `'''`,
`<!--`/`-->`, `--`/`---`), whitespace collapse, signature reduced to its first line. `EligibleKinds` is a
declaration-kind allowlist (function, method, constructor, class, interface, struct, record, enum, delegate,
trait, protocol, union, type_alias) — kind-driven, never a language blocklist. Test symbols get cards;
`is_test` rides the input. `EmbedTextHash` is `sha256:<hex>` over the constructed text. `ChunkText` applies the
`chunks-v1` truncation policy (4,000 chars ≈ 1,024 tokens) to docs/config chunk bodies.

**`src/Miller.Indexing/Semantic/VectorConvergePlanner.cs` (pure).**
- `Plan(VectorConvergeRequest) → VectorConvergePlan`: hash-gates candidates against stored `embed_text_hash`,
  computes the delete set (stored units on changed paths no longer live), and returns `AdvanceTo` (0 = hold).
- Escalation order: delta-history-missing → artifact-id-changed → identity `ShadowRebuild` → batch-too-large →
  changed-ratio. `corpus_generation` (`TargetedReEmbed`) and `ReaderGate` deliberately do NOT escalate, per the
  invalidation matrix and the B2 lead-accepted `writer_version`-only ⟹ `ReaderGate` judgment. Shadow escalation
  is a *decision enum only* — B5 executes it.
- `EvaluateChunkCursor(ChunkCursorFacts) → ChunkCursorDecision` implements all four preconditions in the fixed
  order: (1) artifact binding, with reset-before-comparison; (2) content schema + chunker agreement, the chunker
  derived from the `chunks-v<n>` component of `corpus_generation` (unknown component ⟹ hold, never accept);
  (3) ordering within the bound artifact; (4) per-source hash agreement, normalized, deferring disagreeing paths.
- An empty stored corpus never escalates by ratio (that is the initial build, not a bulk refactor).

**`src/Miller.Server/Hosting/VectorConvergeService.cs`.**
- `VectorConvergeSignal`: capacity-1 coalescing wake, highest-target-wins, inert on a bool check when semantic
  is off.
- `VectorConvergeService` (BackgroundService): constructor reads NO bootstrap getter; `ExecuteAsync` returns
  immediately when the sidecar is disabled (off-guarantee), then waits → drains. Per cursor:
  snapshot-under-gate → chunk gate → plan → embed in bounded batches (64) **outside any gate** →
  re-validate identity + `artifact_id` → commit. Each cursor has an independent try/catch and its own
  `*_last_error` / `*_last_error_at`; a blocked chunk cursor never stalls symbols. Poison (flagged) units are
  left unwritten so the next drain retries exactly them, and the cursor holds. Persisted reasons are scrubbed
  (paths → `<path>`) and bounded to 300 chars.
- `SqliteVectorConvergePort`: the production port. Creates the artifact on first run via `VectorStore.Create`,
  reads the symbol corpus from `symbols.db` (parent join for container, `doc_comment` for the excerpt, eligible
  kinds only), the chunk corpus from `content.db` (`status='active'`, `content_kind IN (workspace_docs,
  workspace_config)` — exactly the `IsDocsLike` partition), the four chunk-cursor facts, and commits deletes +
  inserts + mapping + cursor advance in **one short transaction**.

**Wiring.** `IndexerSidecarConverger` gained an optional `VectorConvergeSignal` and stamps + wakes after the
existing sidecar converges (cheap, stays under the ops gate). `MillerServiceRegistration` registers
`VectorSidecar`, the shared signal, and the hosted `VectorConvergeService` after the M3 services.

## Judgment calls

1. **Signal reached via `VectorConvergeSignal.Shared`, not DI.** `IndexerSidecarConverger`'s only construction
   site is inside `IndexerService.cs`, which I do not own. Rather than edit an unowned file, the converger takes
   an optional signal defaulting to the process-wide `Shared` instance (one leader + one drain loop per
   process). If B5/B6 touches `IndexerService`, injecting it properly is a one-line improvement.
2. **The port owns its own SQL instead of composing over `VectorStore`.** This is the plan mismatch the brief
   anticipated. `VectorStore` exposes `Upsert` (its own per-unit transaction) and `SetMeta`, and has **no
   delete-by-unit-id and no rowid lookup** — so deletions cannot be composed at all, and a cursor advance
   composed as a separate `SetMeta` would violate the load-bearing "one short transaction containing the vec0
   deletes, inserts, mapping updates and the cursor advance". I chose contract fidelity: the port opens its own
   connection (reusing `VectorStore.ResolveExtensionPath`, `MillerSemanticContract.ParseStorageSchema`, and
   `VectorStore.Create` for DDL/initial meta) and commits atomically. Cost: ~40 lines duplicating
   connection-open/vec_version verification and the meta→identity projection (`VectorStore.IdentityFrom` and
   `NormalizeVecVersion` are `internal` to Miller.Indexing). **Recommendation for B5:** fold a batch
   `CommitBatch(kind, vectors, deletes, metaUpdates)` surface into `VectorStore` and delete the duplication.
3. **Initial build runs through the incremental path.** `completed_revision == 0` snapshots as a full pass
   (`FullPass`) over the whole corpus rather than an empty delta, so a fresh artifact actually populates.
4. **The int8 quantizer lives in `VectorConvergeService`** (`QuantizeToInt8`, round×127 clamped to ±127). The
   reader side (lane C) will need the same function — it should move to a shared helper then.
5. **`ChunkTextBudget` / `ChunkText` live on `SymbolCardBuilder`** because it is the owned pure text-construction
   file; the type is really "corpus text builder". Rename is cosmetic and deferred.
6. **`html` emits `kind='class'`** (70 rows in this repo — CSS class attributes), so it will receive cards under
   a kind-driven rule. The language-parity rule forbids a language blocklist, so this is reported as an
   extractor-side kind-mapping question rather than patched in Miller.

## Verification

| Scope | Invariant proven | Command | Result |
|---|---|---|---|
| Card text v1 | Field order, first-line signature, container-less form, doc-marker stripping (10 marker forms), word-boundary truncation at both budgets, hard-cut fallback, kind-driven eligibility, test symbols get cards, hash stability | `dotnet test --filter FullyQualifiedName~SymbolCardBuilder` | 65 passed (with planner) |
| Planner | Unchanged hash ⟹ no work; changed ⟹ exactly the affected unit; new/vanished handled; replay idempotent; all five escalation triggers; ReaderGate never re-embeds; initial build never ratio-escalates | `--filter FullyQualifiedName~VectorConvergePlanner` | passed |
| Chunk cursor | **All four preconditions**: rule 1 both id disagreements + reset-runs-before-ordering (stale higher revision refused); rule 2 schema, chunker, unknown chunker component; rule 3 lag holds / ahead advances; rule 4 hash disagree + missing-in-symbols defer, normalization; reasons carry no path | `--filter FullyQualifiedName~VectorChunkCursorGate` | passed |
| Service | Hash-gated embed; **cursor advance only inside `Commit`** (completed key never written via `SetMeta`); **crash between staged batch and cursor advance leaves a re-runnable state** (cursor unmoved, error recorded, replay converges); replay idempotent; blocked chunk cursor never stalls symbols; independent per-cursor last-errors; chunk artifact-identity reset; in-flight generation change discards the batch; escalation surfaced without embedding; sidecar-unavailable holds; off-mode opens nothing | `--filter FullyQualifiedName~VectorConvergeService\|~VectorConvergeSignal` | 16 passed |
| Combined worker scope | — | the three filters above | **81 passed, 0 failed (57 ms)** |
| Guards | `HostStartupRegistrationTests`, `SemanticOffGuaranteeTests`, `ScaleTraitConventionTests`, `RegistryIsolationConventionTests`, `AgentInstructionsTests` | `--filter …` | **143 passed, 0 failed** |
| Scale | Real `vectors.db` + pinned sqlite-vec + real julie v1 artifact: 2 eligible cards embedded (the `variable` row excluded), cursor at 3, replay embeds nothing; `TryOpen` without the extension returns null | `scripts/test.sh scale` | **73 passed, 0 failed (19 s)** |
| Scale skip path | Skips, never fails, when the extension is absent | `env -u MILLER_SQLITE_VEC_PATH SPIKE_CACHE_DIR=/nonexistent … --filter ~VectorConvergePortScale` | 1 passed, **1 skipped** |
| Build | 0 warnings / 0 errors | `dotnet build Miller.slnx -c Release` | **Build succeeded** |
| Fast suite | Full suite | `scripts/test.sh` | 3923 passed / 2 skipped — see the flake note below |

### Pre-existing flake and ceiling note (investigated, not mine)

`scripts/test.sh` intermittently reported 1–2 failures in `IndexerServiceScanTests` /
`IndexerServiceLeadershipTests` (always a 5 s `ops.ScanCalled.Wait(5000)` timeout, a different test each run),
and sometimes breached the 30 s wall ceiling (21 s–56 s spread).

I did not assume: I created a clean detached worktree at `b7cfc7a` and ran the full suite there six times in
two load windows. Baseline was 3/3 clean in the first window and **2 failures, 1 failure, clean** in the second
— the same tests, the same 5 s timeouts — and produced a 50 s wall time of its own. Running my tree with my new
test classes *excluded* still flaked. Conclusion: an environmental, load-sensitive flake in those leadership
tests that exists at HEAD, plus wall-clock variance from machine load. My additions cost ~120 ms in the fast
suite. The baseline worktree was removed after the experiment (`git worktree list` verified).

One real failure was mine and is fixed: `RegistryIsolationConventionTests` caught the test helper constructing
`IndexBootstrapService` without `TestHomeDirectoryOverride`; it now points at a per-test temp home.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `context "how does the indexer sidecar converger stamp target revisions and wake derived sidecar convergence"` | `IndexerSidecarConverger` is constructed inside `IndexerService` (line 153), not resolved from DI — the fact that drove judgment call 1 |
| `inspect src/Miller.Server/Hosting/IndexerSidecarConverger.cs` | The two-constructor shape (public production / internal seam) the optional signal parameter had to thread through both |
| `inspect ChangedSince depth=full` | Exact signature `IReadOnlyList<RevisionFileChange> ChangedSince(long)`, ordering by `revision_id, path`, and `SymbolSearchSidecar.EnsureCurrent` as the one production consumer — the incremental pattern the planner mirrors |
| `inspect src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` | The B3 surface actually available: `EmbedBatchAsync`, `SemanticEmbedOutcome`, `ISemanticSidecarLauncher`, `ProcessSemanticSidecarLauncher` |

## API-shape evidence

- Card format, budgets, eligibility posture: p2-global-constraints line 11 + design §5.2.
- Four chunk-cursor preconditions, atomic-commit rule, wake-signal capacity: `docs/contracts/vectors-v1.md`
  §Cursors (lines 182–257).
- Escalation triggers 1–5 and `corpus_generation`'s deliberate absence from trigger 3: vectors-v1 lines 259–278.
- Meta key names and mapping-table columns: vectors-v1 §`vectors_meta` / §Mapping tables.
- `content_meta` / `content_sources` / `content_chunks` columns: `src/Miller.Indexing/ContentCorpusSchema.cs`.
- Docs-like partition = `WorkspaceDocs` ∪ `WorkspaceConfig`: `ContentFileClassifier.WorkspaceContentKind`.
- Kind distribution used to justify the eligible-kind set: `sqlite3 .miller/symbols.db "SELECT language, kind,
  COUNT(*) FROM symbols GROUP BY 1,2"` on this repo (json ⟹ `variable`/`module`, markdown ⟹
  `module`/`property`/`import`, yaml ⟹ `variable`/`module` — all excluded by the declaration-kind rule, matching
  the expected outcome in design §5.2).

## Files changed

Created: `src/Miller.Indexing/Semantic/SymbolCardBuilder.cs`,
`src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`,
`tests/Miller.Tests/Indexing/SymbolCardBuilderTests.cs`,
`tests/Miller.Tests/Indexing/VectorConvergePlannerTests.cs`,
`tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, this report.
Modified: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`,
`src/Miller.Server/Hosting/MillerServiceRegistration.cs`.

## Concerns for the lead

1. **`VectorStore` needs a batch-commit surface** (judgment call 2). The duplication is contained in one class
   and is the honest cost of the atomicity invariant, but B5 should absorb it.
2. **No `julie-semantic-sidecar` binary is pinned or packaged yet** — there is no `scripts/semantic-pins.json`
   and no restore script (design §4.3 is a later phase). The production session locator looks under
   `WorkspaceContext.ToolsRoot` and returns null when absent, so the drain does nothing with a stated reason.
   Convergence is not live end-to-end until that binary ships.
3. **`build_state` stays `building`.** Nothing in B4 flips it to `ready`, so a converged artifact is still not
   queryable. That transition belongs with B5's promote / B6's status work — flagging so it is not lost.
4. **`html` `kind='class'` eligibility** (judgment call 6) — a language-parity question for `julie-extractors`.
5. **Escalation is surfaced, not executed** (`VectorConvergeDecision.ShadowRebuild` + trigger on the outcome),
   as the brief scoped. The cursor holds until B5 lands.
