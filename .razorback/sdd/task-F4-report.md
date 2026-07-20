# Task F4 — Semantic rescue + content/source modes

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3` · branch `worktree-semantic-p3`
**Base HEAD:** `7b8b52d` · **Commit:** `26d3a7a` · dirty state at commit: only the two owned files. Never pushed.

## Files

| File | Change |
|---|---|
| `src/Miller.Server/Tools/SearchTool.cs` | modified — new `ISemanticTextArm` seam + `SemanticTextArm`, semantic rescue rung, content-mode rerank, source-mode note |
| `tests/Miller.Tests/Server/SearchToolRescueTests.cs` | created — 22 fast tests |

No other file was touched. `SearchRouteExecutor.cs`, `MillerServiceRegistration.cs`, `RrfFusion.cs` and everything
under `Miller.Indexing` are byte-unchanged (`git show --stat 26d3a7a` lists exactly the two files above).

## Implementation

### The seam (`ISemanticTextArm` / `SemanticTextArm`)

F3's `ISymbolFusionArm` only answers "fuse this lexical symbol ranking". F4's three callers want different things
— an affordance with no lexical list to fuse, a chunk reordering, and a pure "could the artifact have been read?"
probe — so they get their own two-method seam:

```csharp
internal interface ISemanticTextArm
{
    SemanticQueryResult QuerySymbols(string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow);
    SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k);
}
```

The root is **per call**, not per instance (unlike F3's arm), because read tools route by `workspace_id`: the
artifact a query must consult belongs to the workspace that query resolved to. Each call site already holds the
resolved `WorkspaceSymbolSearchContext` / `WorkspaceTextContentSearchContext`, so the root comes free.

`SemanticTextArm` gates on `mode is SemanticMode.On` (**not** `VectorSidecar.Enabled`, which is true under shadow
— the F3 hand-off trap). Under `off`/`shadow` it returns a stated-unavailable result without opening an arm,
launching a sidecar or stat-ing an artifact.

### Production wiring without touching DI

`SearchTool`'s widest **public** constructor gained two optional parameters —
`VectorSidecar? semanticSidecar` and `Lazy<SemanticEmbeddingSession?>? embeddingSession` — and composes the arm via
`SemanticTextArm.For(...)`. Both types are **already registered** by `MillerServiceRegistration`
(`:64` and `:75`), so `WithTools<SearchTool>()`'s `ActivatorUtilities` activation resolves them and the feature is
live in production with **zero DI edits**. An `internal` overload taking `ISemanticTextArm?` directly is what the
tests inject through (`InternalsVisibleTo Miller.Tests`, `Miller.Server.csproj:16`).

This deliberately avoids injecting `WorkspaceContext` into the tool: that registration is
`AddTransient(sp => sp.GetRequiredService<IndexBootstrapService>().Workspace)`, which **throws before bootstrap
binds**. Taking it as a constructor parameter would make every `search` activation resolve it eagerly, converting a
pre-bind call from "handled by the binding filter" into a construction failure. `VectorSidecar` is pure env and
`Lazy<…>` defers its own `WorkspaceContext` read, so both are safe at construction time.
`SearchTool_ActivatesOverTheRegisteredSemanticServices` pins the activation.

### Rescue rung

Inserted as the **last** rung in `TryRunAutoTextRescue`, after the docs/config and source rungs both came back
empty — a lexical hit is evidence the agent can verify by reading, a neighbour is not, so lexical always wins.

- **Trigger:** `_semanticArm is not null` AND `SemanticQueryPolicy.Route(query, LexicalEvidence.None).IsHybrid`.
  The existing `ShouldRunAutoTextRescue` guard already restricts this to compact + `mode=auto` + non-path-like +
  weak-or-empty lexical, so JSON and identifier-shaped queries never reach here.
- **Admission:** a `SymbolVisibilityPolicy` rebuilt from the same `ResolveExcludeTests` /
  `ResolveHideLowSignalKinds` / `ToolSearchFilters.Parse` inputs the lexical route used, passed as the arm's
  `allow` predicate — so rescue cannot surface a test symbol or an out-of-filter file the same query hid.
- **Budget:** `SemanticRescueRows = 2`. One row from each corpus when both answered, otherwise two from whichever
  did — breadth first, because a second neighbour from the same corpus adds much less than the other corpus does.
- **Kinds:** `semantic_symbol` / `semantic_docs` / `semantic_mixed`, returned in `AutoTextRescueResult.Kind` and
  therefore stamped by the **pre-existing** `scope.SetMetadata("auto_rescue_kind", rescue?.Kind ?? "unavailable")`
  line — no telemetry call site changed, so the enum-only guarantee is inherited rather than re-argued.
- **Affordance:** exactly one, per the single-affordance rule. `semantic_symbol` closes with
  `Try: inspect target="<name>"`; docs/mixed close with `Rerun with mode=content …`. `RenderAutoTextRescueCompact`
  (unchanged) already strips the primary output's trailing `next:` nudge.

### `mode=content` hybrid

`RunContentCorpus` gained an optional `rerank` delegate applied **after** escalation and **before** paging, so the
hybrid arm sees the candidate set the lexical arm settled on and can only change which hits make the page — never
which hits exist. Fusion is weighted RRF using `RrfFusion.RankConstant` and `RrfFusion.WeightsFor(HybridClass)`
(the frozen `fusion-v1` constants, read not copied). Tie-breaks: fused score, then lexical score, then path, then
line — total and content-derived.

When the arm is absent or the policy routes lexical-only, `SemanticContentRerank` returns `null` and the branch
calls `SearchRouteExecutor.RunContent` exactly as before — the gated-off path is the *literal* pre-existing code
path, not a re-derivation of it.

### `mode=source` note

Compact-only, appended when the mode is `Source`, the policy says hybrid, and `QueryChunks(root, query, 1).Served`
— i.e. the artifact was **actually consultable**. Never a card, never a symbol row, never in JSON. The note names
the corpus boundary (`the default vector corpus embeds docs/config, not source bodies`) rather than restating that
semantic retrieval is off, which is what makes it honest rather than noise.

## Judgment calls

1. **`LexicalEvidence.None` for the rescue and content routes.** Rescue only runs once the lexical arms came back
   weak or empty — that *is* the evidence the policy would otherwise read off a ranking, and there is no stronger
   lexical signal left to consult. `mode=content` has no symbol ranking at all at decision time. Both therefore
   pass `.None`, which routes shape-ambiguous queries hybrid — the intended behaviour for exactly these rungs.
2. **Chunk hits join content hits on file path.** `SemanticHit` for a chunk carries `DocId` + `FilePath`; the
   content corpus keys on its own `SourceId`/`ChunkId`. The path is the fact both sides agree on regardless of
   chunker identity, and it survives a chunker-version bump. Cost: file-level rather than chunk-level precision in
   the reorder. Recorded as a concern below.
3. **Reorder only, never extend, in `mode=content`.** A semantic-only chunk has no lexical hit to render — no
   score, no line, no snippet. Synthesising one would make a neighbour look like a match, which is the same
   mode-contract dishonesty the `mode=source` rule exists to prevent.
4. **Sync-over-async** in `SemanticTextArm`, contained to two lines, matching F3's precedent.
5. **`SemanticRescueRecall = 8`** — deeper than the 2-row budget so the visibility filter has something to reject
   without collapsing the rung, shallow enough to stay cheap. A defensible first choice, not a tuned one; one
   constant to change if evaluation disagrees.
6. **No rescue row for a chunk whose path repeats.** `Distinct` on path, so two chunks of one doc cannot consume
   the whole budget.

## Verification

| Gate | Command | Red state | Result |
|---|---|---|---|
| Worker red (compile) | `dotnet test --filter FullyQualifiedName~SearchToolRescue` | `CS0246: ISemanticTextArm could not be found` | as expected |
| Worker red (behaviour) | same, with all three rungs short-circuited by a runtime-false guard | **5 failed / 13 passed** — `…RendersLabelledSemanticRows`, `…LabelsThemSemanticDocs`, `…EmitsAtMostTwoRowsAndOneAffordance`, `SourceMode_AppendsTheNotIndexedNote…`, `ContentMode_HybridPromotesTheSemanticallyNearestChunk` | proves the positives are load-bearing, not vacuous |
| Worker green | `dotnet test --filter FullyQualifiedName~SearchToolRescue` | n/a | **22 passed, 0 failed — 123 ms** |
| Golden parity + leadership | `--filter …SearchGoldenParity\|IndexerServiceLeadership\|IndexerServiceScan` | n/a | **71 passed, 0 failed — 483 ms** |
| Full fast suite | `scripts/test.sh` | n/a | **4127 passed, 0 failed, 2 skipped — wall 27s** (clean run; see flake note) |
| Release build | `dotnet build Miller.slnx -c Release` | n/a | **0 warnings / 0 errors** |

Fast-suite cost of this task: ~0.12s (21 pure in-process tests plus one temp-SQLite telemetry theory).

**Known flake, retried per the brief.** Two `scripts/test.sh` runs failed on `IndexerServiceLeadershipTests` /
`IndexerServiceScanTests` — a *different* test each run, all with the documented 5s-timeout signature, while a
parallel Track 1 worker was building in this worktree. They pass in isolation (71/71 above) and the suite passed
clean twice. Nothing in this change touches indexing, leadership or scan.

### What each gate proves, per acceptance criterion

- *Rescue fires only when eligible* — `IsNeverConsultedForJsonOutput` and `IsNeverConsultedForAnIdentifierShapedQuery`
  assert byte-equality against the no-arm render **and** `arm.SymbolQueries == 0`: non-consultation, not merely
  equal bytes. `NeverPreemptsALexicalSourceRescue` proves the rung stays last.
- *≤2 rows, one affordance* — counts labelled lines (exactly 2) and affordance lines (exactly 1) when both corpora
  return two hits each.
- *Telemetry kind stamped, no query text* — a `[Theory]` over all three kinds reads `metadata_json` from a real
  `telemetry.db` and asserts `"auto_rescue_kind":"semantic_…"` is present **and** the query string is absent.
- *`mode=content` reorders under gating* — a BM25-9.0 hit is demonstrably pushed below a BM25-1.0 hit the semantic
  arm ranked first, and both remain present (membership unchanged).
- *`mode=source` never returns a card* — the note is present while `semantic symbol` / `semantic docs` are both
  absent; omitted when the arm is unavailable, when the query is identifier-shaped, and in JSON — each asserted as
  byte-equality against the no-arm output.
- *Off/shadow/unready byte-identical* — unavailable-arm and empty-served-arm cases on the auto, source and content
  routes each `Assert.Equal` the exact no-arm output string. `SemanticTextArm_UnderShadowMode/UnderOffMode` pass a
  factory that **throws if called**, proving neither mode opens an arm.
- *Golden parity* — `SearchGoldenParityTests` (18 cases) passes **unchanged**.

## Miller calls used

| Call | What it proved |
|---|---|
| `inspect target="src/Miller.Server/Tools/SearchTool.cs" limit=120` | Full symbol map of the 112KB file (5 classes, 83 methods, 13 constants) with exact line anchors — the file was never read whole; every subsequent read was a targeted range this listing pinpointed. |
| `inspect target="ISymbolFusionArm" depth=full` | F3's exact seam shape, its doc contract ("null means the lexical bytes must be handed back untouched"), and all 5 reference sites — established that F4 needed a *different* seam rather than an extension. |
| `search`/`grep` for `SemanticSymbolFusionArm` | Located it at `SearchRouteExecutor.cs:233` (outside my ownership) and its DI factory at `MillerServiceRegistration.cs:81` — the evidence that drove the "compose from already-registered services" decision. |

Targeted line-range reads were used only for regions `inspect` had already located inside `SearchTool.cs`
(constructors `:104-183`, the `Search` route branches `:240-395`, the rescue region `:1471-1685`, the content/text
cores `:1150-1400`).

## API-shape evidence

Every upstream contract below was read from committed source, not assumed.

- `SemanticQueryPolicy.Route(string?, LexicalEvidence?) -> SemanticQueryRoute(IsHybrid, HybridClass, Reason)` and
  `LexicalEvidence.None` — used exactly as F3 does: gate on `IsHybrid`, read `HybridClass` only after.
- `SemanticSearchArm.QuerySymbolsAsync(query, k, Func<VectorMatch,bool>?, ct)` /
  `QueryChunksAsync(query, k, allow, ct)` — `SemanticSearchArm.cs:131` / `:142`. Note `QueryChunksAsync` **does**
  accept an allow predicate; F4 passes none because chunk visibility is a path-level concern the content corpus
  filter already applied.
- `SemanticQueryResult(Hits, UnavailableReason)`, `Served => UnavailableReason is null`,
  `SemanticQueryResult.Unavailable(reason)` — `:16-23`. The empty-served / unavailable distinction is load-bearing
  twice: the source-mode note fires on `Served` (artifact readable, corpus simply has no source chunks) and the
  rescue rung treats empty-served as "nothing to show", both distinct from "arm could not run".
- `SemanticHit(SymbolId?, DocId?, FilePath, Rank, Cosine)` — `:9`. Exactly one id is populated; rescue reads
  `SymbolId` on the symbol rung and filters `DocId is not null` on the chunk rung, so a symbol can never be
  rendered as a doc.
- `SemanticSearchArm.MaxCandidates = 500` — `:78`, the content rerank's recall ceiling.
- `SemanticMode { Off, Shadow, On }` — `SemanticActivation.cs:5-16`; `VectorSidecar.Enabled` is `Mode is not Off`
  (**true under shadow**), so every gate here reads `Mode is SemanticMode.On`.
- `VectorMatch(RowId, Distance, UnitId, Path)` — `VectorStore.cs:7`; `UnitId` is what the allow predicate resolves
  through `ISymbolLookupIndex.FindBySymbolId`.
- `RrfFusion.RankConstant = 60`, `RrfFusion.WeightsFor(SemanticFusionClass)` — `RrfFusion.cs:39/45`, both public;
  reused directly so the content route cannot drift from the symbol route's frozen profile.
- `SymbolVisibilityPolicy(bool HideTests, bool HideLowSignalKinds, ToolSearchFilters Filters).Allows(IndexedSymbol)`
  — `SearchTool.cs:119`, F3's addition, reused verbatim for rescue admission.
- `WorkspaceSymbolSearchContext.Index` / `.WorkspaceRoot` and `WorkspaceTextContentSearchContext.WorkspaceRoot` —
  `WorkspaceTextContentSearchContext.cs:9-18`.
- `MillerServiceRegistration.cs:49/64/75` — `WorkspaceContext` (transient, **bootstrap-throwing**), `VectorSidecar`
  (singleton, env-pure), `Lazy<SemanticEmbeddingSession?>` (singleton, lazy). This triple is the whole basis of the
  wiring decision above.

## Concerns / hand-off notes for F5

1. **Path-level chunk matching (judgment call 2).** The content rerank promotes *every* lexical hit in a file the
   semantic arm liked, not just the matching chunk. For a long document with many lexical hits this is coarser than
   intended. Fixing it needs a stable chunk identifier shared between `content.db` and `vectors.db`; worth an
   evaluation datapoint before adding that coupling.
2. **The rescue rung is not observable beyond its kind.** Like F3's arm, an abstention (policy said lexical, arm
   unavailable, nothing found) records nothing distinguishing — `auto_rescue_kind` reads `none`/`unavailable` for
   all of them. F5's determinism contract probably wants the reason enum; `UnavailableReason` is already available
   at the call site.
3. **`--arm` needs a third injection point.** F5 must set/clear `ISemanticTextArm` on the CLI path in addition to
   `SearchRouteExecutionRequest.FusionArm`. The internal `SearchTool` constructor is the seam for that; no new
   public surface is required. `--arm semantic` (semantic-only) remains unexpressible on the content route for the
   same reason F3 flagged: the rerank reorders a lexical list rather than replacing it.
4. **`SemanticRescueRecall` and the 1+1 row split are untuned** (judgment calls 5, 6) — single constants.
5. **No MCP parameter or tool was added.** The arm is composed from `MILLER_SEMANTIC` only.
