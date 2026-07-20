# Task B6 — Status/health facts, telemetry, canary-contract plumbing

Worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch `worktree-semantic-p2`,
clean at start (HEAD `5655dd1`), sole worker, no push (2026-07-20 directive).

## What was implemented

### 1. The full vectors-v1 §Status vocabulary, produced from real artifact facts

`VectorSidecarFacts` grew from `(State, Path, Reason)` into the fact record the whole surface needs — build
progress, both cursors, the five generation-identity fields, the artifact id, the serving generation and the
retained inventory — all as additive init-only members, so every existing construction site is untouched.

`VectorSidecar.Classify` was split into `ClassifyGeneration(path, role, tag)` plus a resolver:

- **Every state is populated from meta that was already read.** Cursors (`{symbol,chunk}_completed_revision` /
  `_target_revision` / `_last_error` / `_last_error_at`), `build_progress_percent`, `artifact_id` and the
  identity fields cost nothing extra — the meta read already happened for the reader gates.
- **`incompatible` now means what the contract says it means.** Per vectors-v1 §Status, `incompatible` is "no
  generation — active *or* retained — matches the reader". When the active generation is not `ready`, the
  retained `vectors.gen-*.db` inventory is classified in turn and the first ready-and-compatible one becomes
  the serving generation (`ServingRole = "retained"`). Conformance clause 6's "a reader whose encoder matches a
  retained generation serves from it across process restarts" is now true of the status surface, not just of
  `VectorGenerationManager`'s retention.
- **`circuit-open` / `disk-blocked`** are read from `vectors_meta.converge_pause_state` (+ `_reason`), an
  additive key on the STRICT key-value table. A pause is an artifact-mediated fact deliberately, not an
  in-process one: `workspace status` is usually answered by a *reader* instance, and an in-process session flag
  would report "healthy" from every process except the leader that tripped it. An unrecognised pause value is
  ignored rather than rendered verbatim, so a future writer cannot inject a non-vocabulary string.
- **`disabled` still renders nowhere** (lead ruling 1). Off assembles the fact and both formats stay
  byte-identical to a no-semantic build — now pinned by four tests (compact + JSON, status + health).

### 2. Pending-file counts, resolved where the extract lives

`ready (updating; N files pending)` needs a *file* count, and `vectors_meta` only holds revisions. The count is
resolved in the assembler — the only layer that holds the `symbols.db` path — through the existing
`RevisionDeltaReader`, keyed on the cursor's `completed_revision` and the generation's own `artifact_id`:

- a caught-up cursor is `0` **without** touching the journal;
- a behind cursor counts the distinct changed paths in its span;
- a span the journal cannot vouch for (`pruned_history`, `artifact_changed`, …) leaves `pending_files` **null**
  — unknown, never a guessed zero — and the compact line falls back to plain `ready` while JSON still carries
  the exact revisions. This is the same honest-span posture `RevisionDeltaReader` already takes.

The seam is `WorkspaceFactsAssembler.WithPendingFiles(facts, readDelta)`: pure, delegate-driven, fast-suite
testable with no sqlite; the production overload binds the delegate to `RevisionDeltaReader.Read`.

`VectorSidecarFacts.LaggierCursor` picks the reported cursor: pending files decide when both counts are known,
revision lag decides otherwise, so an unreconstructable delta still picks the cursor that is further behind.

### 3. Render seam

Compact renders exactly the frozen strings and nothing more. JSON gained (additive, still omitted entirely when
`disabled`/absent): `build_progress_percent`, `serving_tag`, `serving_role`, `artifact_id`, `symbol_cursor`,
`chunk_cursor`, `identity` (all six identity values), `retained_generations`. Serving-generation identity is
JSON-only — the compact line says `ready` whether the active or a retained generation answers (lead ruling 3).
Both contract docs now describe the block.

### 4. Canary telemetry plumbing (`src/Miller.Server/Telemetry/CanaryTelemetry.cs`)

Verbatim `canary-telemetry-v1` field names, complete enum value sets, the frozen assignment derivation
(`SHA256(experiment|version|workspace|date|class)`, big-endian uint32 % 100), the frozen latency-bucket edges,
and the three served-result hash arrays capped at 10 with one shared truncation flag — using the *same* digest
mechanism as `TelemetryScope.SetTarget`, proven by a test that matches a follow-up `inspect` row's `target_hash`
against the qualified array.

Inert by construction:

- `MILLER_SEMANTIC_CANARY` defaults **off**, and off writes not one key.
- `CanaryAssignment.ResolveArm` returns the constant `control` for every bucket. The bucket is persisted anyway,
  so P5 replaces one method body — no telemetry migration.
- Nothing calls `CanaryTelemetry.Stamp` in production. Wiring it into `SearchTool` would be activation and
  belongs to lane C/P5; the write path is exercised only by tests.

`TelemetryScope` gained one overload (`SetMetadata(string, IReadOnlyList<string>)`) for the hash arrays.

## Judgment calls

1. **`TagsWithLiveReaders` stays unwired; GC is soak-window-protected in P2** (lead ruling 4, my decision).
   Rationale: readers are *separate processes*. An in-process live-reader set would protect the GC-running
   leader's own handles and silently miss every other instance — protection that looks real and is not, which is
   worse than none. Real coverage needs a durable cross-process registration (heartbeat rows or per-tag lease
   files) with its own liveness and staleness rules, and that belongs with the GC *scheduler*, which does not
   exist yet either (B5 concern 3 — nothing calls `CollectGarbage`). The fail-safe posture today is genuinely
   safe: the 24h soak window plus "never delete the only ready generation" plus the retention cap already make
   deletion of a generation someone is reading essentially impossible, and `PlanGarbageCollection` already
   honours `TagsWithLiveReaders` when a producer eventually supplies it. Recorded as the P2 posture; the
   registration lands with the GC scheduler in P4.
2. **Retained-generation fallback shipped in B6, not deferred.** It is the definition of `incompatible` in the
   very §Status table B6 owns, so shipping the vocabulary without it would render `incompatible` for a workspace
   that is in fact serving. It is contained to `Classify` and adds no new I/O seam.
3. **`converge_pause_state` is a new `vectors_meta` key.** vectors-v1 tabulates meta keys but the table is a
   STRICT key-value store and every reader uses `GetValueOrDefault`, so an added key breaks nothing and no
   reader misreads it. The alternative — an in-process signal — cannot answer from a reader instance. Flagged
   below: the **consumer is complete, the producer is not mine to write** (it belongs in
   `VectorConvergeService`, B5's file).
4. **`VectorSidecar.cs` was modified although the brief lists only assembler/render/telemetry.** `Inspect` is
   the sole producer of these facts and the sole owner of the off-guarantee seam; enriching facts anywhere else
   would mean a second meta reader outside the off-switch. Reported rather than silently redesigned.
5. **Pending files live in the assembler, not the sidecar.** The sidecar owns the vectors artifact; the extract
   delta journal is the assembler's data. Keeping the read there also keeps `VectorSidecar` free of a
   `symbols.db` dependency.

## Verification

| Scope | Invariant proven | Command | Result |
|---|---|---|---|
| Vocabulary (compact) | Each of `ready`, `ready (updating; N files pending)`, `building N% (not queryable)`, `unavailable (reason)`, `incompatible`, `circuit-open`, `disk-blocked`, `downloading` renders exactly the frozen string | `--filter ~WorkspaceVectorFacts` | passed |
| Laggier cursor | The reported count is the laggier cursor's, by pending files when known and by revision lag otherwise | ″ | passed |
| Retained serving | Serving from a retained generation still renders plain `ready`; the tag/role appear only in JSON | ″ | passed |
| JSON exactness | Per-cursor revisions/errors, all six identity fields, artifact id, serving tag/role, retained inventory | ″ | passed |
| Off byte-identity | Compact **and** JSON, status **and** health, are byte-identical with `disabled` vs no vectors at all | ″ | passed |
| Classification | Cursors/identity/progress parsed from meta; pause states override; unknown pause ignored; retained fallback serves, and stays `incompatible` when nothing matches | `--filter ~VectorSidecar` | passed |
| Pending files | Caught-up ⟹ 0 with no journal read; behind ⟹ changed-path count; unreconstructable span ⟹ null; `disabled` ⟹ untouched | `--filter ~WorkspaceFactsAssembler` | passed |
| Canary contract | Verbatim field names, complete enums, frozen derivation + bucket edges, absent-vs-zero, ineligible rows carry only reason+class, hash arrays capped with shared flag, follow-up attribution matches `target_hash` | `--filter ~Canary` | passed |
| Canary privacy | Persisted `metadata_json` contains no query text, symbol name, path, or qualified spelling (D1 `StampedFailureBucket` forbidden-text pattern) | ″ | passed |
| Canary durability | A stamped row survives to `tool_telemetry.metadata_json` with `"canary_arm":"control"` | ″ | passed |
| Worker scope | — | `--filter "~VectorSidecar\|~WorkspaceVectorFacts\|~WorkspaceFactsAssembler\|~Canary"` | **78 passed, 0 failed (89 ms)** |
| Adjacent seams | Render, telemetry, status/health JSON contract tests unaffected | `--filter "~WorkspaceRender\|~Telemetry\|~WorkspaceStatus\|~WorkspaceHealth\|~Contract"` (with guards below) | **373 passed, 0 failed** |
| Guards | `SemanticOffGuarantee`, `AgentInstructions`, `HostStartupRegistration` | included in the run above | passed |
| Fast suite | Whole fast suite | `scripts/test.sh` | **4004 passed / 2 skipped, 23 s duration, 27 s wall** |
| Scale suite | Real sqlite-vec + real julie-extract | `scripts/test.sh scale` | **75 passed, 0 failed (20 s)** |
| Build | 0 warnings / 0 errors | `dotnet build Miller.slnx -c Release` | **Build succeeded** |

The 41 new fast tests cost ~90 ms. One first `scripts/test.sh` run reported 42 s wall (over the 30 s tripwire)
while a Release build of the other projects was still contending; the immediately following quiet run was 27 s
wall / 23 s duration, in line with B5's 21–23 s. Reported rather than hidden: the tripwire did fire once.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `context query="vectors status facts assembler render telemetry canary semantic"` | The real seam set: `WorkspaceFactsAssembler` lives under `Tools/` not `Hosting/` (brief's path was wrong), and `WorkspaceRender.Status`/`StatusCompact` + `WorkspaceTool.RenderStatus` are the only render entry points |
| `inspect src/Miller.Server/Tools/WorkspaceFactsAssembler.cs` | Four fact-construction sites (registered / unregistered / missing-index / unreadable-index) — all four needed the pending-file enrichment, not just the happy path |
| `trace VectorSidecarFacts refs` | After the change, the fact record's only production consumers are `VectorSidecar.ClassifyGeneration`, `WorkspaceRender.VectorsLabel`/`WriteVectorsJson` and `WorkspaceFactsAssembler.WithPendingFiles` — no dashboard or CLI consumer was broken by widening the record |

## API-shape evidence

- Status vocabulary strings, the laggier-cursor rule and the JSON-only list: `docs/contracts/vectors-v1.md:280`,
  `:580-610`, conformance clause 6 at `:625`.
- `vectors_meta` cursor/build keys: `docs/contracts/vectors-v1.md` §`vectors_meta` table.
- Canary field names, enums, derivation, bucket edges, hash-array cap and privacy rule:
  `docs/contracts/canary-telemetry-v1.md` §Assignment, §Enums, §Field Reference, §Result Identifiers.
- Delta-journal semantics (honest span failure, artifact-identity guard):
  `src/Miller.Indexing/RevisionDeltaReader.cs:20-115`.
- Retention/GC inputs including `TagsWithLiveReaders`: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs:53-70`.

## Files changed

Modified: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`,
`src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Server/Telemetry/TelemetryScope.cs`,
`tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`, `docs/contracts/workspace-status-v1.md`,
`docs/contracts/workspace-health-v1.md`.
Created: `src/Miller.Server/Telemetry/CanaryTelemetry.cs`,
`tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs`,
`tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs`,
`tests/Miller.Tests/Server/CanaryTelemetryTests.cs`, this report.

Commit: `f4f44cc` — `feat(semantic): status/health vector facts, canary telemetry plumbing (P2 B6)`.

## Concerns — what P4's shadow rollout still lacks for diagnosability

1. **`circuit-open` and `disk-blocked` have no producer.** The consumer, precedence and render are complete and
   tested, but nothing writes `converge_pause_state`. The circuit exists (`SemanticSessionState.CircuitOpen`) and
   the drain loop holds the session; stamping the pause on the artifact is a few lines in
   `VectorConvergeService` — outside my ownership. **Until that lands, a paused convergence reports `ready
   (updating; N)` forever with no reason**, which is exactly the shadow-rollout failure mode hardest to
   diagnose. Highest-value P4 follow-up.
2. **No disk-space preflight exists at all** (vectors-v1 §Preflight). `disk-blocked` cannot occur today because
   nothing checks free space before a build; the state is renderable, the guard is unimplemented.
3. **`downloading` has no producer either.** The sidecar's model acquisition is not wired (no
   `julie-semantic-sidecar` binary is pinned — carried forward from B4/B5), so a model download currently shows
   as `unavailable` with the sidecar's reason rather than `downloading`.
4. **GC still has no caller and no live-reader registry** (judgment call 1). Retained generations accumulate
   until a P4 scheduler runs `CollectGarbage`; that same slice should supply `TagsWithLiveReaders`.
5. **Canary rows are never written in production.** By design for P2, but it means the canary schema has no
   field-shaped evidence behind it: the first real rows appear when P5 wires `Stamp` into the search surface.
   The write path is fully unit-covered, so the risk is integration-shaped, not schema-shaped.
6. **Pending-file counts are per-status-call sqlite reads.** Only when semantic is enabled *and* a cursor is
   behind, and `RevisionDeltaReader` is the same query `impact --from-index-revision` already runs — but a
   very hot status loop on a lagging workspace pays it every call. Cheap to cache later if it shows up.
