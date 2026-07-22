# retrieval-eval

Scoring harness for Miller's semantic-retrieval evaluation protocol
([design §8](../../docs/plans/2026-07-19-miller-semantic-integration-design.md)).

The harness is **pure scoring**. It never embeds, never queries an index, and never talks to Miller. A
benchmark arm runs its own retrieval and writes a results JSONL; this tool reads that file plus the golden
query set and emits a report. That separation is what lets one frozen ground truth score every arm — model,
dimension, quantization lane, fusion weights — on identical math.

It is deliberately **outside `Miller.slnx`** and depends on nothing but `System.Text.Json`. Product builds do
not see it.

## Usage

```bash
# score an arm's results against the dev golden set
dotnet run --project eval/retrieval-eval -- score \
  --queries eval/retrieval-eval/sets/dev/queries.jsonl \
  --results /path/to/arm-results.jsonl \
  --out /path/to/report.json \
  --corpus miller=/Users/murphy/source/miller \
  --corpus julie=/Users/murphy/source/julie \
  --k 10

# check a query set's schema, composition minimums, and that every graded doc still exists
dotnet run --project eval/retrieval-eval -- validate \
  --queries eval/retrieval-eval/sets/dev/queries.jsonl \
  --corpus miller=/Users/murphy/source/miller \
  --corpus julie=/Users/murphy/source/julie

# score a user-owned paired task-completion event
dotnet run --project eval/retrieval-eval -- task-score \
  --tasks /sealed/task-manifest.jsonl \
  --baseline /sealed/baseline-results.jsonl \
  --candidate /sealed/candidate-results.jsonl \
  --out /sealed/task-aggregate.json

# score stabilized Miller-vs-Julie agent-efficiency runs
dotnet run --project eval/retrieval-eval -- agent-score \
  --tasks /sealed/agent-tasks.jsonl \
  --miller /sealed/miller-runs.jsonl \
  --julie /sealed/julie-runs.jsonl \
  --out /sealed/agent-aggregate.json
```

`--k` defaults to 10. `--corpus` is optional for `score` (it adds a `corpus_validation` block) and takes
either a bare directory (applies to every repo) or repeated `<repo>=<dir>` pairs for a multi-repo set.

Exit codes: `0` ok, `1` usage/IO error, `2` validation failed.

`task-score` exits `0` for every valid aggregate, including `fail` and `underpowered` verdicts. Missing
arguments and file-system failures exit `1`; malformed, unsupported, duplicate, or mismatched rows exit `2`.

`agent-score` also exits `0` for every valid verdict. It stabilizes initial completion disagreements with
exactly three repetitions per arm, requires Miller completion non-regression with no critical losses, and
then evaluates output-token or tool-call savings under the p75 wall-time guard. Its report contains aggregate
cells and sufficiently populated groups only; it never emits task ids, prompts, answers, evidence, or paths.

Tests: `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj`.

## Query-set schema (`queries.jsonl`)

One JSON object per line. Blank lines and `#` comment lines are skipped.

| field | type | notes |
| --- | --- | --- |
| `query_id` | string | unique across the set |
| `query` | string | the literal text handed to the retrieval arm |
| `intent_cluster` | string \| null | paraphrases of one intent share a cluster id; null for standalone queries |
| `query_class` | enum | `identifier` \| `path` \| `short_token` \| `prose` \| `docs_like` \| `mixed` |
| `search_mode` | enum | optional; `auto` by default, or `symbol` \| `file` \| `content` \| `source`; frozen identically across arms |
| `repo` | string | selects the corpus root for doc resolution |
| `language` | string | drives per-language macro-average and worst-language reporting |
| `relevant` | array | `{"doc_id": string, "grade": 1..3}`; empty for negatives |
| `negative` | bool | true means "nothing in this repo should be returned" |
| `tags` | string[] | optional; `negation` and `ambiguous` are counted by the composition check |
| `note` | string | optional; used here to record the lexical failure mode a query was mined from |

`doc_id` is a repo-relative file path, optionally suffixed `#SymbolName`. Grades: **3** the file that answers
the query, **2** strongly relevant, **1** supporting context.

## Results schema (`results.jsonl`) — the arm contract

One JSON object per line, one per query the arm ran:

```json
{"query_id": "m-promote-1", "ranked": ["src/Miller.Indexing/FullRebuildPromotion.cs", "src/Miller.Indexing/JulieExtractRunner.cs"]}
```

`ranked` is the arm's ordered `doc_id` list, best first, in the same `doc_id` vocabulary as the query set.

Rules an arm must honor:

- **Emit results post-threshold.** Only include a doc the arm would actually show a user. Negative-query
  scoring depends on this: returning anything for a negative query counts as a false positive.
- A query with no results row scores zero and is listed under `missing_results` — an omitted row is never
  silently treated as "not applicable".
- Rows whose `query_id` is not in the query set are ignored and listed under `unknown_results`.
- Duplicate `query_id`s in either file are a hard error.

## Metrics

- **recall@k** — fraction of a query's relevant docs appearing in the first `k` ranked entries.
- **nDCG@k** — graded, with exponential gain `2^grade - 1` and `log2(position + 2)` discount, normalized by
  the ideal ordering (grades sorted descending) truncated at the same `k`.

### The evaluation unit (read this before comparing arms)

Design §8 requires paraphrase intent clusters to be **scored as clusters, not independent samples**. The
harness therefore builds an **evaluation unit** list and averages the primary metrics over units:

- each non-empty `intent_cluster` is **one unit**, whose recall/nDCG is the **mean over its member
  paraphrases** — the expected quality over a random phrasing of that intent;
- each positive query with no `intent_cluster` is **one unit**.

`unit_policy` in the report names this (`cluster`), and every block carries both `unit_count` and the
`query_count` those units cover. This is a decision-relevant choice, not bookkeeping: a mined cluster with
five paraphrases would otherwise outvote five distinct intents, and on the dev set the per-query and
cluster-unit rankings of the benchmark lanes genuinely disagree.

**Primary (cluster units):** `overall`, `per_language`, `language_macro_average`, `worst_language`.

**Secondary:**

- `overall_per_query` — every positive query weighted equally. Useful as a reference, not for pins.
- `overall_cluster_max` — cluster units taking their **best** member's score: "is this intent reachable by
  *some* phrasing?" Read with `intent_cluster_summary`, which is the hit-coverage version of the same view.
- `per_language_per_query` — the per-query reference view of `per_language`.

`per_query_class` stays **per-query** by construction: query classes cut across clusters, so there is no
cluster unit to average over. Report labels must say so.

- **Cluster hit coverage** — `cluster_hit` is true when **any** member paraphrase retrieved at least one
  relevant doc inside `k`. `member_hit_rate` is reported alongside so a cluster that only survives on its
  most literal phrasing is visible rather than hidden behind a hit.
- **Language macro-average** — the mean of per-language (cluster-unit) means, so a language with three
  queries counts as much as one with forty. `worst_language` reports the lowest-scoring language (nDCG
  first, then recall). Both are required by the language-parity rule: a headline average that hides one
  broken language is a regression. A cluster is attributed to its members' dominant language.
- **Negatives** — a negative query passes when the arm returns **no doc inside `k`**. Because results files
  are post-threshold, "returned something" *is* "made a confident claim", so the harness needs no scores. The
  report gives `false_positive_rate` and `pass_rate`.
- **Per-query-class breakdown** — the `identifier` block is the non-inferiority set: hybrid retrieval must not
  degrade it relative to the lexical baseline arm.

## Report shape

`score` writes a JSON object with `k`, `unit_policy`, query counts, `evaluation_unit_count`, `overall`,
`search_mode_counts`, `overall_per_query`, `overall_cluster_max`, `per_language`, `per_language_per_query`,
`language_macro_average`, `worst_language`, `per_query_class`, `per_intent_cluster`,
`intent_cluster_summary`, `negatives`, `missing_results`, `unknown_results`, `inputs`, and (when
`--corpus` is given) `corpus_validation`.

## Paired task-completion scoring

Task completion is the primary offline value measure for the semantic maturity decision. It is separate from
retrieval recall/nDCG: a user-controlled coordinator runs the same sealed task under both arms, then this pure
scorer compares the paired outcomes without reading task prompts, acceptance checks, trajectories, or output.

The task manifest accepts exactly these fields:

| field | type | notes |
| --- | --- | --- |
| `task_id` | string | unique opaque pairing key; never emitted |
| `repo` | string | non-sensitive short repo label, never a path |
| `language` | string | non-sensitive language-family label, never a path |
| `query_profile` | enum | `identifier` \| `path` \| `short_token` \| `prose` \| `docs_like` \| `mixed` |

Each baseline and candidate result file accepts exactly:

| field | type | notes |
| --- | --- | --- |
| `task_id` | string | must match the manifest exactly |
| `completed` | bool | result of the pre-frozen acceptance check |
| `duration_ms` | integer | nonnegative elapsed time |
| `tool_calls` | integer | nonnegative total calls |
| `search_calls` | integer | nonnegative and no greater than `tool_calls` |
| `zero_result_search_calls` | integer | nonnegative and no greater than `search_calls` |

The schema 1 output contains `inputs` (lowercase SHA-256 digests only), `pair_count`, the four completion
cells, `primary_gate`, `identifier_path_safety`, aggregate arm diagnostics, and sufficiently populated
repo/language/query-profile groups. It contains no task id, input path, prompt, check, trajectory, result text,
or per-task row. Valid fail/underpowered reports are evidence, not process errors, so they still exit `0`.

The primary gate requires at least 30 pairs and a two-sided 95% Wilson lower bound above 0.5 for candidate
wins among discordant pairs. The identifier/path safety subset is underpowered below five pairs and fails on a
baseline-only reversal. Groups below five pairs are suppressed. See
[`sets/SEALED-TASK-PROTOCOL.md`](sets/SEALED-TASK-PROTOCOL.md) before creating or running a task event.

## Set-construction protocol

1. **Pin the corpus first.** Record repo paths and full commit SHAs in `sets/dev/manifest.json` before
   labeling anything. Ground truth that drifts under a re-tuning pass is worthless.
2. **Mine paraphrases from real lexical failures.** Run Miller `search` (`mode=auto`, `mode=source`,
   `mode=content`) with prose phrasings of a subsystem you already understand and record what the lexical
   backend returns. Phrasings that miss the right file are the material a semantic arm has to earn —
   `note` fields in the dev set record the observed miss.
3. **Verify every reference.** Each `doc_id` must be confirmed to exist via Miller `search`/`inspect` and then
   by `validate --corpus`, which resolves it on disk at the pinned commit.
4. **Grade conservatively.** Reserve grade 3 for the file a competent engineer would open first.
5. **Keep the floors.** `CompositionMinimums.Dev` encodes them (≥60 queries; ≥6 clusters per repo with ≥3
   paraphrases each; ≥15 identifier; ≥5 short-token; ≥5 negation/ambiguous-tagged; ≥5 negatives), and
   `DevSetTests` fails the build if the shipped set drops below.
6. **Never tune against the sealed set.** See [`sets/SEALED-SET-PROTOCOL.md`](sets/SEALED-SET-PROTOCOL.md).

## Sets

- `sets/dev/` — the visible dev set (82 queries across miller + julie) and its manifest. Frozen for tuning.
- `sets/SEALED-SET-PROTOCOL.md` — how the user-owned acceptance set is held and used. No sealed data lives in
  this repo.
- `sets/SEALED-TASK-PROTOCOL.md` — how the user-owned blinded paired task event is frozen, run, and returned as
  aggregates only.

## Consumers

[`eval/model-bench/`](../model-bench/README.md) is the first arm producer: it embeds a symbol-card and
docs-chunk corpus with each candidate model, ranks the dev set, and scores every model/dims/quantization
lane plus a BM25 baseline through this harness. Its output is the P0 pin recommendation in
[`docs/findings/2026-07-19-model-benchmark.md`](../../docs/findings/2026-07-19-model-benchmark.md).

Two contract notes that any future arm producer should inherit from it:

- **Exclude the golden set from your corpus.** `sets/**` lives inside the miller workspace and contains both
  query text and answer paths. An arm that indexes `eval/`, `.razorback/`, or `.claude/` retrieves its own
  answer key and scores meaninglessly high. Miller now excludes the exact nested `.claude/worktrees/**`
  path, but other `.claude/` material remains in scope; benchmark corpora must still exclude all three roots.
- **Honor the post-threshold rule literally.** `ranked` must contain only docs the arm would actually show.
  An arm that emits its raw top-k scores a false positive on every negative query, which is correct
  behavior for the metric but makes negatives incomparable across arms unless the threshold policy is
  stated. model-bench records its floor/ratio policy per arm for exactly this reason.
