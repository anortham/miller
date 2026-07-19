# Task 6 report — Retrieval eval harness + dev golden set

**Status:** COMPLETE
**Commit SHA:** none - parallel-lead-commit

> Note: this path previously held a stale report from an unrelated run ("Task 6 — Telemetry panel polish",
> worktree `dashboard-ux-fixes`). `.razorback/sdd/` is gitignored scratch and `task-6-brief.md` in this
> directory is this run's brief, so the stale file was leftover and has been overwritten.

## Implementation

A dependency-free (System.Text.Json only) console harness plus its own test project, living entirely outside
`Miller.slnx`, and a verified 82-query dev golden set spanning the miller and julie repos.

### Harness (`eval/retrieval-eval/`)

- `Model.cs` — query/result row records. Query rows carry `query_id`, `query`, `intent_cluster`,
  `query_class`, `repo`, `language`, `relevant[{doc_id,grade}]`, `negative`, plus optional `tags` and `note`.
- `Metrics.cs` — pure metric math. `RecallAtK` (distinct relevant hits inside the cutoff / total relevant);
  `NdcgAtK` (graded, exponential gain `2^grade - 1`, `log2(pos+2)` discount, normalized by the ideal ordering
  truncated at the same k).
- `Scorer.cs` — rollups: overall, per-language, language macro-average (mean of per-language means, so a
  3-query language weighs the same as a 40-query one), worst-language (lowest nDCG, then recall), per-
  query_class (the `identifier` block is the non-inferiority set), per-intent-cluster, negatives, plus
  `missing_results` / `unknown_results` bookkeeping. Duplicate query ids in either file are a hard error.
- `Report.cs` — the report JSON shape.
- `QuerySetValidator.cs` — schema rules + `CompositionMinimums.Dev` (the design §8 floors, encoded so the
  test suite fails if the shipped set ever drops below them).
- `CorpusChecker.cs` — resolves every graded `doc_id` on disk at the pinned checkout.
- `Program.cs` — `score` and `validate` verbs.

**Negatives rule (defined and documented):** a negative query passes when the arm returns **no doc inside k**.
This is sound because the results file is specified as *post-threshold* — an arm emits a doc only if it would
show it to a user — so "returned something" is precisely "made a confident claim". Reported as
`false_positive_rate` / `pass_rate`. Documented in README.md and in the `NegativeBlock` doc comment.

### CLI contract (Task 7's integration surface)

```
dotnet run --project eval/retrieval-eval -- score \
  --queries <q.jsonl> --results <r.jsonl> --out <report.json> \
  [--corpus <dir> | --corpus <repo>=<dir> ...] [--k 10]

dotnet run --project eval/retrieval-eval -- validate --queries <q.jsonl> [--corpus ...]
```

Results row: `{"query_id": "...", "ranked": ["doc_id", ...]}`. Exit codes 0 ok / 1 usage-IO / 2 validation
failed. `--corpus` accepts a bare dir (all repos) or repeated `repo=dir` pairs — needed because the dev set
spans two repos while the brief's contract specifies a single `--corpus` flag; the bare form is unchanged.

Report JSON contains: `k`, query counts, `overall`, `per_language`, `language_macro_average`,
`worst_language`, `per_query_class`, `per_intent_cluster`, `intent_cluster_summary`, `negatives`,
`missing_results`, `unknown_results`, `inputs`, `corpus_validation`.

## Verification

**Invariant:** scoring math correct + dev set valid per composition minimums with every reference verified.

| Scope | Command | Result |
| --- | --- | --- |
| Harness unit + e2e tests | `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj` | **26 passed, 0 failed** (184 ms) |
| Dev-set validation | `dotnet run --project eval/retrieval-eval -- validate --queries eval/retrieval-eval/sets/dev/queries.jsonl --corpus miller=/Users/murphy/source/miller --corpus julie=/Users/murphy/source/julie` | **82 queries, 38 distinct doc references checked, 0 missing, composition minimums met** |
| Synthetic e2e `score` over the real dev set (perfect-oracle results) | `dotnet run --project eval/retrieval-eval -- score …` | recall@10=1.0000, ndcg@10=1.0000, macro-avg 1.0000 over 3 languages, worst csharp 1.0000, 14/14 clusters hit, negatives FPR 0.0000 (0/6), identifier n=16 recall 1.0000 |
| Product build unaffected | `dotnet build Miller.slnx -c Release` | **Build succeeded, 0 Warning(s), 0 Error(s)** |

Timestamp: 2026-07-19. Worktree: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`,
branch `worktree-semantic-integration`. Work started at commit `87f9b1d`; the lead landed sibling tasks
during the run, so the final verification (26/26 tests, Release build 0 warnings / 0 errors) was re-run at
`6a2bffc`. My files are outside `Miller.slnx`, so the product build is unaffected either way.

Metric tests assert real hand-computed values, not "it ran": exponential-gain nDCG against
`(7/1 + 1/2) / (7/1 + 1/log2 3)`, ideal-ordering reordering, cutoff truncation, macro-average that provably
differs from the micro-average (0.375 vs 0.5), cluster-max scoring (hit with member_hit_rate 1/3), and the
negatives cutoff boundary. The e2e test deserializes the written report file and checks every rollup block.

One test failure occurred during development and was a **bug in my hand-computed expectation**, not in the
implementation (I asserted csharp nDCG 0.0 where the correct value is (0 + 1.0)/2 = 0.5). Corrected in the
test; the implementation was right.

## Dev-set composition

82 queries, 76 positive / 6 negative, 38 distinct doc references, all verified to exist.

| Dimension | Counts | Minimum | Met |
| --- | --- | --- | --- |
| Total queries | 82 | ≥60 | ✅ |
| Repos | miller 41, julie 41 | miller + julie | ✅ |
| Intent clusters | 14 total — miller 7, julie 7 | ≥6 per repo | ✅ |
| Paraphrases per cluster | 3 for all 14 | ≥3 | ✅ |
| Identifier queries | 16 (miller 8, julie 8) | ≥15 | ✅ |
| Short-token queries | 6 (miller 3, julie 3) | ≥5 | ✅ |
| Negation/ambiguous tagged | 6 (miller 3, julie 3) | ≥5 | ✅ |
| Irrelevant negatives | 6 (miller 3, julie 3) | ≥5 | ✅ |

Query classes (all rows): prose 53, identifier 16, short_token 6, docs_like 4, path 2, mixed 1.
Positive-query languages: csharp 36, rust 36, markdown 4 — three languages, so macro-average and
worst-language are both meaningful.

Clusters — miller: `full-rebuild-promotion`, `version-aware-leadership`, `sensitive-root-guard`,
`search-ranking-parity`, `cli-vs-server-dispatch`, `host-lifecycle-contract`, `single-writer-lock`.
julie: `root-safety`, `file-watcher-queue`, `embedding-sidecar`, `db-init-lock`, `search-scoring`,
`symbol-editing`, `search-tokenizer`.

Manifest pins: miller `/Users/murphy/source/miller` @ `97485d4f0ba8d3a03c8893fe39405a8e77a90b86` (main),
julie `/Users/murphy/source/julie` @ `0744b93013ca3eea374c78064a4d0f054cedc99a` (main).

## Files changed

Created (all new, all under `eval/retrieval-eval/`):

- `RetrievalEval.csproj`, `Program.cs`, `Model.cs`, `Metrics.cs`, `Scorer.cs`, `Report.cs`, `Jsonl.cs`,
  `QuerySetValidator.cs`, `CorpusChecker.cs`
- `tests/RetrievalEval.Tests.csproj`, `tests/MetricsTests.cs`, `tests/ScorerTests.cs`,
  `tests/EndToEndTests.cs`, `tests/DevSetTests.cs`
- `README.md`, `sets/dev/queries.jsonl`, `sets/dev/manifest.json`, `sets/SEALED-SET-PROTOCOL.md`

No file outside `eval/retrieval-eval/**` was touched (except this report). `bin/`/`obj/` are covered by the
existing `.gitignore`.

## Miller calls used (Miller-first, both repos)

- `workspace list` — found both target workspaces registered (`miller-b275269b2d7c`, `julie-316c0b0829f9`);
  no `workspace open` needed.
- `workspace status workspace_id=julie-316c0b0829f9` — julie index fresh, rev 17, 34,204 symbols.
- `workspace refresh workspace_id=miller-b275269b2d7c` — returned `lock_busy` (another process holds that
  checkout's indexer lock) and its search sidecar was stale, so symbol discovery ran against the **current**
  worktree workspace, which is fresh and shares the same `src/` tree. File existence was then proven
  independently against the pinned `/Users/murphy/source/miller` checkout by the corpus checker.
- `inspect` (confirmed definition + path for): `FullRebuildPromotion`, `LeadershipEligibility`,
  `WorkspaceRootSafety`, `Bm25`, `CliDispatch`, `SymbolSearchSidecar`, `MillerServiceRegistration`,
  `src/Miller.Indexing/JulieExtractRunner.cs`.
- `search` on miller (paraphrase mining): "how does the indexer decide it can take over as leader", "why does
  a forced rescan build into a separate database file and swap it in", "refuse to index the home directory or
  filesystem root", "how are search results scored and ranked", "decide whether to run as a command line tool
  or start the server", "telemetry ledger", "what happens when two miller processes fight over the writer
  lock", "where do i change the pinned extractor version"; plus `mode=content` and `mode=file` probes.
- `search workspace_id=julie-316c0b0829f9` (cross-workspace, per the miller-cross-workspace rule): "how does
  the tool decide a workspace root is unsafe to index", "embedding vector storage and similarity search",
  "incremental file watcher reindex on change", "generate text embeddings with a local model", "parse source
  files with tree-sitter and pull out symbols", "acquire an exclusive lock so only one process writes the
  index", "fuzzy match a symbol name when the spelling is close", `mode=file` "string_similarity".

**Mined lexical misses recorded as `note` fields** (this is the paraphrase material the semantic arm must
earn):

- "how does the indexer decide it can take over as leader" → returned `LeaderWriteThrough` / `IndexerService`;
  `LeadershipEligibility` never appeared.
- "why does a forced rescan build into a separate database file and swap it in" → returned `docs/site/index.html`
  and `FreshnessService.PollAndSwap`; not `FullRebuildPromotion`.
- "how are search results scored and ranked" → returned python benchmark scripts and `ContentSearchIndex.ScoredHit`;
  not `Bm25`.
- "decide whether to run as a command line tool or start the server" → returned site CSS/HTML and a python
  reporting script; not `CliDispatch`.
- "refuse to index the home directory or filesystem root" → symbol arm returned `IndexBootstrapService` /
  `PathCanonicalizer`; only the source-region rescue reached `WorkspaceTool.Open`.
- `mode=file` on the exact filename `ADR-0001-guidance-delivery-channels` → **no match**, although the file
  exists at the pinned checkout.
- julie "who owns the model that turns code into vectors" → site HTML/CSS and `health_types.rs`.

## API-shape evidence

- `Directory.Build.props` at the repo root applies to `eval/**` (net10.0, nullable, `TreatWarningsAsErrors`),
  so the harness builds under the same zero-warning bar; confirmed by a clean build.
- `Miller.slnx` lists projects explicitly (`src/*`, `tests/Miller.Tests`, `tools/Miller.SearchQuality`), so
  `eval/retrieval-eval` is genuinely outside the product solution — verified by a clean Release build.
- Test packages match the repo's existing pins from `tests/Miller.Tests/Miller.Tests.csproj`
  (`xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.6.0), and xUnit v3 needs
  `OutputType=Exe`, as that csproj documents.
- There is no `Directory.Packages.props`, so per-project `PackageReference` versions are correct here.

## Judgment calls

1. **Test project location.** `tests/` sits inside `eval/retrieval-eval/` so `dotnet run --project
   eval/retrieval-eval` still resolves one project file; the console csproj excludes `tests/**` and `sets/**`
   from its globs.
2. **`--corpus` accepts `repo=dir` pairs.** The brief specifies a single `--corpus <dir>`, but the dev set
   spans two repos. The bare form is preserved verbatim (applies to every repo); the repeated pair form is
   additive, so Task 7's documented invocation is unaffected.
3. **Miller pinned to main HEAD, not the worktree branch.** The dev set references only files that exist at
   `97485d4` on `/Users/murphy/source/miller`. Discovery used the worktree workspace (the main checkout's
   sidecar was stale and its lock busy), but every reference was verified on the pinned checkout. Nothing
   branch-only — notably the untracked `docs/adr/ADR-0003-*.md` — is referenced.
4. **`tags` field added to the query schema.** The brief requires ≥5 negation/ambiguous queries, but the
   `query_class` enum (fixed by Task 5's contract) has no such member. Rather than widen the enum, negation
   and ambiguity are marked with an optional `tags` array and counted by the composition check. The enum is
   untouched.
5. **Grade scale 1–3** with 3 = the file a competent engineer opens first. Documented in the README.
6. **Negatives are scored by absence at the cutoff**, which requires arms to emit post-threshold results.
   The alternative — scoring by confidence — would have forced a score field into the results contract that
   Task 7 does not currently produce.
7. **julie's working tree was dirty** at construction (`TODO.md`, `.codex/config.toml`). Neither is
   referenced by any query; the manifest records the dirty state and that fact explicitly rather than
   claiming a clean pin.

## Concerns

1. **Non-inferiority set is 16 queries.** It clears the ≥15 floor but is small for detecting a modest
   identifier-quality regression. If P4/P5 wants a tight non-inferiority bound, this block should grow.
2. **Two code languages (csharp, rust) plus markdown.** Worst-language reporting works, but the design's
   language-parity ambition is broader than two repos can supply. The sealed set is the natural place to add
   a third code language, and doing so also satisfies leave-one-repo-out.
3. **The dev set is now indexed by Miller.** `sets/dev/queries.jsonl` contains the query text and the answer
   paths, and it lives inside the miller workspace — so a future arm run against the miller repo could
   retrieve the golden set itself. Arms should exclude `eval/retrieval-eval/sets/**` from their corpus.
4. **`--corpus` proves file existence, not symbol existence.** `doc_id#Symbol` suffixes are permitted by the
   schema but the current dev set uses file-level ids only, so this gap is latent rather than active.
