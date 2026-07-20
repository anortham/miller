# Task E1 report — MinHash/LSH near-duplicate analyzer

**Status:** COMPLETE. `commit SHA: none - parallel-lead-commit`

## Worktree state

- Path: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
- Branch: `worktree-semantic-p2` @ `8c37dad` (ahead 1 of origin), dirty with parallel-worker edits (C1/D1/B1 own files I did not touch)
- No `git add` / `git commit` / push performed.

## Implementation

### 1. `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs` (new, pure)

`NearDuplicateAnalyzer.FindGroups(IReadOnlyList<NearDuplicateInput>, NearDuplicateOptions?) -> IReadOnlyList<NearDuplicateGroup>`.

Pipeline: raw-body exact-class collapse → normalization → token shingling → MinHash signature → LSH banding →
exact Jaccard on candidate pairs → union-find grouping → deterministic ordering → `MaxGroups` cap.

Determinism: no `Random`, no clock, no `string.GetHashCode`. Hashing is a locally implemented FNV-1a 64
(shingle + band keys) and SplitMix64 (permutations, seeds). All arithmetic is `unchecked ulong`, so results are
identical across processes and platforms. Input **order** also does not affect output (candidates are
ordinal-sorted before matching; union-find always roots at the lower index; groups sort on a total order).

**Exact-vs-near boundary:** bodies whose RAW text is byte-identical collapse to one representative (ordinally
first id) *before* matching. Those are exactly what `CloneGroupReader` reports, so they can never form a
near-duplicate group on their own and never appear twice inside one. Collapsing on raw (not normalized) text is
deliberate: two bodies differing only in identifier names normalize to the same token stream and ARE the
Type-2 case we must report.

### 2. `src/Miller.Server/Tools/MetricsTool.cs` (thin wiring)

- `Run(...)` gains a trailing optional `bool nearDuplicates = false`.
- `RunClones` keeps the exact arm untouched, then — only when the flag is on and a workspace root is known —
  reads a bounded candidate set and hands bodies to the analyzer.
- Candidate query: `symbols` rows with non-null body spans and `body_end_byte - body_start_byte >= 160`,
  ordered `(path, start_line, symbol_id)`, `LIMIT 2000`.
- Body text comes from `ExtractReader.ReadBody`, which enforces the design §7 freshness invariant: a file that
  drifted from the indexed content is skipped, never sliced stale. A stale workspace therefore yields FEWER
  groups, never wrong ones (test-proven).
- Rendering: compact gains a `# near-duplicate groups` section; JSON appends `kind`/`similarity`-bearing group
  objects to the same `groups` array.

### 3. `src/Miller.Server/Cli/CliDispatch.cs` (flag plumbing)

`--near-duplicates` added to the `metrics` flag set and usage string, passed as `nearDuplicates:`.

## Chosen constants and rationale

| Constant | Value | Rationale |
|---|---|---|
| `ShingleSize` | 5 tokens | Large enough that generic punctuation runs (`) { return`) don't collide; small enough to survive a local edit inside a body. |
| `MinTokens` | 24 | Below this a body is too small for an honest similarity claim — a two-line accessor matches every other accessor. Such bodies are skipped entirely. |
| `SignatureLength` | 128 | Standard MinHash width; estimator error ~1/√128 ≈ 9%, and since it is only used for candidate PRUNING (similarity is exact Jaccard) the width only affects recall, never a reported number. |
| `BandCount` × `RowsPerBand` | 32 × 4 | Product = 128. The LSH knee (1/b)^(1/r) = (1/32)^(1/4) ≈ 0.42 sits well below the 0.75 threshold, so threshold-grade pairs are reliably proposed as candidates. |
| `SeedBase` | `0x9E3779B97F4A7C15` | The golden-ratio constant, used as the SplitMix64 chain seed for the 128 permutation seeds. A fixed published constant beats an arbitrary one for reproducibility. |
| `DefaultMinSimilarity` | 0.75 | High enough that a reported pair is a real rename-or-retune of the same code rather than two functions of similar shape. |
| `DefaultMaxGroups` | 50 | Mirrors `MetricsTool.DefaultLimit`; `metrics clones --limit N` overrides it. |
| Keyword set | 150 words | Fixed cross-language reserved-word list kept verbatim (lowercased) during normalization. Without it every `if`/`return`/`for` skeleton matches everything. Deliberately a SET, not a per-language table: an absent word simply becomes the identifier placeholder — a little lost structure, never a mis-attributed language. |
| `NearDuplicateCandidateCap` | 2000 | Each candidate costs one hash-verified disk body read; a bigger sweep belongs in a background arm, not a CLI verb. |
| `NearDuplicateMinBodyBytes` | 160 | Byte floor mirroring the analyzer's token floor — filters in SQL so hopeless candidates never cost a disk read. |
| `similarity` rounding | 4 dp, `MidpointRounding.ToEven` | Stable rendered text; banker's rounding is deterministic. |

Reported `similarity` is the **exact Jaccard of the shingle sets** (MinHash prunes, it never decides), so the
number carries no estimator error. For a multi-member group it is the **weakest accepted edge** that linked the
group — a floor, documented as such, not an average.

## Judgment calls

1. **Opt-in flag, default OFF.** The brief did not specify activation. The Type-2 arm re-reads symbol bodies
   from disk with a per-file BLAKE3 verification, so riding along on every `metrics clones` would be a real
   latency regression on a large repo. Default-off also makes the "existing output byte-stable" guarantee
   unconditional rather than contingent on the analyzer finding nothing. This matches the lane's opt-in /
   zero-work-by-default posture. **Lead: flip the default if you want it always-on.**
2. **`kind` on near-duplicate groups only.** Exact groups are emitted byte-for-byte as in v1 — no `kind` field
   added — because adding one would break byte-stability on every existing run. Near-duplicate entries are
   appended to the same `groups` array carrying `kind: "near_duplicate"` + `similarity`. An absent `kind`
   therefore means the v1 exact `body_hash` group. This is the "alongside the exact groups" shape the brief
   asked for, kept additive.
3. **Candidate SQL lives in `MetricsTool.cs`.** No bulk symbol-body-span reader exists in `Miller.Indexing`, and
   my file ownership forbade creating one. `Miller.Server` has precedent for direct read-only SQLite
   (`TelemetryExportReader`). The query runs only after `CloneGroupReader.Read` has already passed the D5 schema
   gate on the same artifact. **Recommended follow-up (not mine to make): extract a
   `Miller.Indexing/NearDuplicateCandidateReader`.**
4. **Whitespace-only twins ARE reported** (similarity 1.0). My first test asserted the opposite; that was wrong.
   Formatting-only duplicates are genuine Type-2 clones that the exact `body_hash` surface misses, so surfacing
   them is the feature working, not a false positive. Test corrected.
5. **Comments are not stripped** (that is language-specific and would need extractor facts). Documented as a
   known limitation in the analyzer doc comment: a body differing only in comment prose can land in a group.

## Concerns for the lead

- `docs/contracts/metrics-json-v1.md` **needs a Clones-section update** for `kind` / `similarity` and the
  `--near-duplicates` flag. It is outside my file ownership, so I did not edit it.
- Task E2 (`near_duplicate_group_count` history metric) will need a group count from a path that does not pay
  the disk-read cost on every converge. Today the count only exists behind the opt-in CLI flag.
- The candidate cap of 2000 is a silent bound: on a repo with more eligible symbols, later files (by path order)
  are never examined and nothing says so. Consider surfacing a truncation note if E2 promotes this to a
  recorded metric.

## Verification

**Invariant:** with the Type-2 arm off, `metrics clones` compact and JSON output is byte-identical to v1 and
costs zero extra work; with it on, exact groups are still emitted first and unchanged, near-duplicate groups are
appended, and identical bodies are never double-reported.

| Scope | Command | Result |
|---|---|---|
| Worker scope | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~NearDuplicate\|FullyQualifiedName~MetricsTool"` | **44 passed, 0 failed** |
| + CLI flag | same with `\|FullyQualifiedName~Metrics_Clones` | **46 passed, 0 failed** |
| Fast suite (ceiling) | `scripts/test.sh` | **3731 passed, 0 failed, 1 skipped, 28s** |
| Build | `dotnet build Miller.slnx -c Release` | **0 warnings / 0 errors** |

Timestamp: 2026-07-20. Scale suite not run (out of scope, per brief).

### Tests added

`tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs` (12): Type-2 rename+literal pair grouped; unrelated
bodies not grouped; identical bodies left to the exact surface; identical-body class contributes one
representative; below-token-floor bodies skipped; repeat-run determinism; input-order independence; threshold
rejection; `MaxGroups` bound; whitespace-only twins at similarity 1.0; empty input; transitive grouping reports
the weakest edge.

`tests/Miller.Tests/Server/MetricsToolTests.cs` (6): flag-off JSON and compact unchanged; flag-on JSON emits
`kind`/`similarity`/ordered symbols; flag-on compact renders the section; exact and near groups side by side
with exact first and no `kind`; drifted workspace file yields no groups rather than stale ones.

`tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (1): `--near-duplicates` accepted, exit 0, empty stderr,
exact group still first.

## Files changed

| File | Change |
|---|---|
| `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs` | created |
| `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs` | created |
| `src/Miller.Server/Tools/MetricsTool.cs` | modified |
| `tests/Miller.Tests/Server/MetricsToolTests.cs` | modified |
| `src/Miller.Server/Cli/CliDispatch.cs` | modified (flag plumbing) |
| `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | modified |

`src/Miller.Indexing/CloneGroupReader.cs` — **untouched**, as required.

## Miller calls and what they confirmed

| Call | Confirmed |
|---|---|
| `inspect src/Miller.Indexing/CloneGroupReader.cs depth=full` | 3 types + `Read`; `DefaultSymbolsPerGroup=25`, `MaxSymbolsPerGroup=500` |
| `context "metrics clones exact clone groups rendering compact and json output"` | Seeds: `RenderClonesCompact:268`, `RenderClonesJson:345`, `RunClones:120`, `MetricsToolResult:663` |
| `inspect CloneGroupReader.Read depth=full` | Exact SQL: groups on `symbols.body_hash`, `ROW_NUMBER() OVER (PARTITION BY body_hash)`, order `(path, start_line, symbol_id)`. **No body TEXT is read** — only `body_hash`. |
| `inspect RunClones depth=full` | 5 params, clamps symbol limit, single caller `Run:76` |
| `search "s.body FROM symbols" mode=source` | v1 `symbols` stores no body text; `SearchIndexWriter` keeps tokens in `symbols_fts` |
| `search "code_context" mode=source` | `JulieDbFixtureCurrentSchemaTests` proves `code_context` was REMOVED from v1 `symbols` — body text must be re-sourced from disk |
| `inspect ExtractReader.ReadBody depth=full` | Signature `(dbPath, workspaceRoot, filePath, startByte, endByte, startLine, endLine) -> BodyReadResult`; enforces the BLAKE3 freshness invariant; returns `Unavailable(StaleFile)` on drift |
| `inspect src/Miller.Indexing/ExtractReader.cs` | No bulk body/span reader exists (15 methods, all single-symbol or refs) |
| `inspect SymbolExportReader depth=full` | Deterministic `(path, start_line, symbol_id)` ordering convention for symbol feeds — reused for candidate ordering |
| `inspect IndexedSourceTextReader depth=full` | Only `FindLiteral`; not a body source |
| `search "read many symbols with body spans bulk export symbol rows"` | Confirmed no existing bulk span reader → justified the local candidate query |

## API-shape evidence

- `CloneGroup(string BodyHash, int Count, IReadOnlyList<CloneSymbol> Symbols)` — `CloneGroupReader.cs:97`
- `CloneSymbol(SymbolId, Name, Kind, Language, Path, Line, IsTest)` — `CloneGroupReader.cs:99` (reused verbatim
  for near-duplicate group members, so both group kinds render identical symbol shapes)
- `MetricsToolResult(string Output, int ResultCount, IReadOnlyList<MetricHistoryPoint>? SnapshotMetrics)` —
  `MetricsTool.cs:850`
- v1 clones JSON fields `schema_version / operation / groups[] {body_hash, count, symbol_limit,
  symbols_truncated, symbols[]}` — read from `RenderClonesJson` and cross-checked against
  `docs/contracts/metrics-json-v1.md` §Clones
- `symbols` columns `body_start_byte, body_end_byte, body_start_line, body_end_line, body_hash` — confirmed in
  `ExtractReader.ReadEditSpan` SQL (`ExtractReader.cs:116`) and `JulieDbFixture` `SymbolsDdl:1167`
- CLI flag parsing: `CliOptions.Parse(args, "json", "include-tests", "exclude-tests", "include-commits")` at
  `CliDispatch.cs:648` — extended with `"near-duplicates"`; `workspaceRoot: ctx.WorkspaceRoot` already flowed
  into `MetricsTool.Run`, so no new context plumbing was needed

## Self-review

- Analyzer has zero I/O and lives in `Miller.Core`; `Miller.Core`'s zero-I/O-dependency seam is intact.
- `MetricsTool` is wiring plus rendering only; all detection logic is in the pure analyzer and unit-tested there.
- No new MCP tool, no MCP parameter, no `ServerInstructions` change — `AgentInstructionsTests` green in the fast
  suite. The surface is CLI-only, matching the metrics contract.
- `CloneGroupReader` byte-for-byte untouched (verified: not in the changed-file set).
- Every fixed constant is a named `public const` on the analyzer and named in its doc comment, so the
  determinism contract is readable without reading the algorithm.
- Comment discipline followed: doc comments on public types and on the non-obvious wiring invariants (schema
  gate ordering, why the arm is off by default, why the JSON is shaped as it is); no narration comments.
- Nothing pushed; no unrelated worker files touched. Two build interruptions from parallel workers' in-flight
  edits (`TextReplaceMatcher.cs`, `SearchTool.cs`) were waited out, not worked around.
