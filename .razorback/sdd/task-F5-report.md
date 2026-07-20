# Task F5 report — CLI `--arm` + determinism contract

Worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`, base HEAD `af8460a`.

## Files

| File | Change |
| --- | --- |
| `src/Miller.Server/Cli/CliDispatch.cs` | modified — `--arm` flag, symbol-route arm composition, `CliSearchArm`, `CliSemanticSession`, `ForcedHybridFusionArm`, `CliSemanticRender` |
| `tests/Miller.Tests/Server/SearchDeterminismTests.cs` | created — 13 facts/theory cases |
| `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | extended — 8 new cases (`Search_*Arm*`, usage) |

No other file touched. No MCP tool parameter, no tool `[Description]` edit, no new public type or public method.

## Implementation

`search` now parses `--arm lexical|semantic|hybrid`. Absent flag ⟹ `CliSearchArm.Policy`.

Dispatch (`RunSymbolRoute`, private, called only after the symbol index loads):

- **Policy (absent flag)** — composes the *same* production arm `MillerServiceRegistration` composes:
  `new SemanticSymbolFusionArm(sidecar.Mode, () => new SemanticSearchArm(root, sidecar, session.Open))`. Under
  `MILLER_SEMANTIC=off` (the default) the branch returns before building the sidecar arm at all, so the flagless
  CLI path is byte-identical to today. Under `on` the CLI and a tool call now route one query the same way.
- **Lexical** — `RunSymbols` with no `FusionArm`, i.e. today's path, whatever the mode/artifact would allow.
- **Hybrid** — `request with { FusionArm = new ForcedHybridFusionArm(() => arm) }`.
- **Semantic** — `SemanticSearchArm.QuerySymbolsAsync` called directly from the CLI, rendered by
  `CliSemanticRender.Symbols` (rank / cosine / name / kind / path:line; JSON is a bare array). Neither
  `ISymbolFusionArm` nor `SemanticSymbolFusionArm` was extended for it — per hand-off fact #2 the CLI owns its
  evaluation rendering.

Loud failure for forced `semantic|hybrid`, in order, each `return 3` with the reason on stderr and nothing on stdout:
1. `sidecar.Mode is not SemanticMode.On` → names `MILLER_SEMANTIC` and the current mode.
2. `sidecar.TryOpen(root, out reason)` returns null → emits the sidecar's own stated reason.
3. `SemanticSearchArm.ProcessSession(ToolsRoot)` returns null → names the missing pinned binary and the restore script.
4. `--arm semantic` whose query result is unserved → emits `UnavailableReason`.

`--arm semantic|hybrid` on a non-symbol route (`content`/`source`/`regions`/`markers`/`external`/`web`/`all-text`)
is exit 2 with a stated reason, consistent with the global constraint that `mode=source` stays lexical-only.
Unknown `--arm` value is exit 2. Usage and `miller help` strings are additive.

## Judgment calls

1. **`--arm hybrid` bypasses the policy gate, via a CLI-local arm.** The brief says hybrid "forces fusion
   regardless of policy", but `SemanticSymbolFusionArm` abstains on `!route.IsHybrid` — installing it would let a
   symbol-lookup query silently answer lexically, defeating the evaluation flag. `ForcedHybridFusionArm` lives in
   `CliDispatch.cs` (my file) and drops only the mode and `IsHybrid` gates; it keeps the visibility predicate,
   the `MinimumRecall`/`MaxCandidates` clamp, the index-resolution drop, and the frozen `fusion-v1` weights —
   still keyed on `SemanticQueryPolicy.Route(...).HybridClass`, so a forced run is scored under the profile a
   routed run would have used. Deliberately *not* a change to the F3 seam. Cost: ~25 lines duplicated from
   `SemanticSymbolFusionArm.Fuse`. Pinned by `ForcedHybrid_FusesEvenWhenThePolicyWouldRouteLexicalOnly`.
2. **The flagless path now composes the production arm.** The brief's "absent flag = policy routing" and the
   constraint's "absent flag = exact existing behavior" reconcile only because the default mode is `Off`: under
   `off`/`shadow` nothing is built or stat-ed and output is byte-identical, and under `on` the CLI stops being
   accidentally lexical-only. Pinned by `Search_ArmLexical_RendersExactlyTheDefaultOutput`.
3. **Gate on `SemanticMode.On`, never `VectorSidecar.Enabled`** (the stated trap) — `Enabled` is true under
   shadow. Every branch above tests `Mode is not SemanticMode.On`.
4. **`CliSemanticSession`.** The CLI is one-shot, so the embedding session it may open is opened at most once and
   `DisposeAsync`-ed before the verb returns, rather than leaking a child process. The server keeps its singleton.
5. **Cosine is fixed-precision.** `Math.Round(c, 4)` in JSON and `F4`/`InvariantCulture` in compact — a
   culture-sensitive decimal separator alone would break byte-identity between two runs on different machines.
6. **`--arm semantic` on an unserved result exits 3 rather than rendering an empty list**, so the renderer only
   ever handles served hits; the reason goes to stderr.

## Verification

Red states observed before implementing:
- `SearchDeterminismTests.cs` + the new `CliDispatchTests` cases: **compile-red**, 7 errors —
  `CS0246 CliSearchArm`, `CS0246 ForcedHybridFusionArm`, `CS0103 CliSearchArm` ×5.
- After the seam types landed: `CS0051` inconsistent accessibility (public `[Theory]` parameter of internal enum)
  → rewritten as a `[Fact]` over an internal helper.

Green:

| Gate | Result |
| --- | --- |
| `dotnet test --filter "FullyQualifiedName~SearchDeterminism\|FullyQualifiedName~CliDispatch"` | **168 passed, 0 failed** |
| `--filter SearchGoldenParity\|SearchDeterminism\|CliDispatch\|HybridSearch\|AgentInstructions` | **254 passed, 0 failed** |
| `dotnet build Miller.slnx -c Release` | **Build succeeded, 0 warnings / 0 errors** |
| `scripts/test.sh` (fast suite) | 4151 passed, 2 skipped, **2 failed — both foreign** (below), 24 s wall |

Gate invariants held: `SearchGoldenParityTests` (18 cases) unchanged and green; `HybridSearchTests` green;
`AgentInstructionsTests` green (no tool description touched); fast-suite wall 24 s vs the 30 s ceiling — the
13 added determinism cases plus 8 CLI cases cost well under 1 s (the whole `SearchDeterminism|CliDispatch`
filter runs in 8 s including build, and the determinism file uses only in-process fakes).

Foreign fast-suite failures, **not fixed** per the ownership rule:
1. `IndexerServiceLeadershipTests.StartAsync_ArtifactMatchesOwn_RunsOnlyTheStartupDeltaScan` — the known
   under-load flake. Retried in isolation: **23 passed, 0 failed**.
2. `ScaleTraitConventionTests.EveryPinnedBinarySpawningTest_IsTaggedScale...` — fails naming
   `Indexing/TempGuardProbe.cs`. `ScaleTraitConventionTests.cs`, `ScaleTestSupport.cs` and the new
   `SemanticSidecarScaleTests.cs` are all dirty in this shared worktree and are impl-g3's Track 1 files. Left
   untouched.

## Miller calls

- `context query="CliDispatch search verb argument parsing and search execution" token_budget=2500` — located
  `CliDispatch.cs:29` and `CliDispatchTests.cs:30/52` (the `Run` harness) without reading either whole file.
- `search query="SemanticSymbolFusionArm" limit=8` — pinned `SearchRouteExecutor.cs:233` (class), `:237` (ctor),
  `:38` (`ISymbolFusionArm`), and `SearchTool.cs:165/166` (the two arm fields).
- `inspect target="src/Miller.Server/Tools/SearchTool.cs" limit=40` — full symbol map (4 ctors incl. the internal
  one at `:228`, `ISemanticTextArm` at `:26`, `SemanticTextArm.For` at `:48`) without reading the 2 000-line file.

Targeted `sed`/`grep` reads only for the ~140 relevant `CliDispatch.cs` lines (the file is 2 534 lines; never read whole).

## API-shape evidence

- The brief's `SemanticSymbolFusionArm(SemanticMode, Func<SemanticSearchArm>)` is the primary ctor
  (`SearchRouteExecutor.cs:233`); a `(SemanticMode, SemanticSearchArm)` convenience overload exists at `:237`.
  The Miller index initially surfaced only the overload — verified against the file before relying on it.
- `SemanticSearchArm.ProcessSession(string toolsRoot)` returns `SemanticEmbeddingSession?` (null when the pinned
  binary is absent), and `SemanticEmbeddingSession` exposes `DisposeAsync` only — no synchronous `Dispose`.
- `VectorSidecar.TryOpen(string, out string?)` returns a `VectorStore?` the **caller must dispose**; the null path
  always populates the reason. Used as a pure readiness probe (opened and disposed immediately).
- `SemanticActivation.FromEnvValue` maps blank/unrecognised to `SemanticMode.Off` — which is what makes the
  flagless CLI path a provable no-op by default.

## Concerns

1. `ForcedHybridFusionArm` duplicates the retrieve-resolve-fuse body of `SemanticSymbolFusionArm`. If the
   production arm's recall clamp or resolution rules change, this copy must follow. The alternative — a gate-skip
   flag on the production arm — would put an evaluation-only concern inside the F3 seam, which the hand-off
   forbids. Flagging for pre-merge review as a deliberate trade.
2. `--arm semantic|hybrid` is only reachable end-to-end with a real `vectors.db` + sqlite-vec, so the fast suite
   proves the parse, the route gate, all four loud-failure branches, and the render/fusion determinism, but not a
   live artifact query. That path belongs in a Scale test against a real sidecar (Track 1 territory) — worth a
   follow-up if the branch gate wants live `--arm` coverage.
3. The shared worktree currently carries impl-g3's in-flight Track 1 edits; the fast suite will keep reporting the
   `ScaleTraitConventionTests` failure until they land their tag.
