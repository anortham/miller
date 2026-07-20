### Task 6: Retrieval eval harness + dev golden set

**Files:**
- Create: `eval/retrieval-eval/` — harness project (console, NOT in Miller.slnx), `eval/retrieval-eval/README.md` (usage + set-construction protocol), `eval/retrieval-eval/sets/dev/*.jsonl` (dev golden set), `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: harness CLI contract Task 7 depends on: `dotnet run --project eval/retrieval-eval -- score --corpus <dir> --queries <jsonl> --results <jsonl> --out <report.json>` where `--results` is arm output in a defined JSONL shape (`query_id`, ranked `doc_id` list), and the report contains recall@10, nDCG@10, per-language macro-average, worst-language, per-intent-cluster rollup. Also the query-set JSONL schema: `query_id`, `intent_cluster`, `query_class` (same enum as Task 5), `repo`, `language`, `relevant` (doc ids + grades), `negative` (bool).

**Contract inputs:** Design §8 (eval protocol): intent clusters scored as clusters; macro-average AND worst-language; negatives included; sealed-set separation.

**File ownership:** Create: `eval/retrieval-eval/**` (harness project + fixtures + docs)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** (a) The scoring harness — pure computation over JSONL inputs, no embedding calls (backends produce results files; the harness only scores). Unit-tested metric math (recall@k, nDCG@k with graded relevance, cluster-max scoring: a cluster counts as hit if any paraphrase in it retrieves a relevant doc). (b) The dev golden set: ≥60 queries spanning miller + julie repos: ≥6 paraphrase intent clusters per repo (3+ paraphrases each), ≥15 identifier queries (non-inferiority set), ≥5 short-token, ≥5 negation/ambiguous, ≥5 irrelevant negatives; every query labeled with `query_class`, language, and graded relevant docs (symbol ids/file paths verified against the real repos with Miller). (c) `SEALED-SET-PROTOCOL.md`: the acceptance set is user-owned, same schema, stored outside the repo until evaluation events, never used during tuning; document the handoff procedure.

**Approach:** Keep the harness dependency-free (System.Text.Json only). Seed dev queries from the design's documented failure modes (paraphrase queries that lexical search currently misses — mine candidates by running Miller `search mode=source` for prose phrasings of known subsystems and recording misses).

**Acceptance criteria:**
- [ ] Harness scores a synthetic fixture correctly (unit tests for recall@k, nDCG@k, cluster scoring, macro/worst-language rollups)
- [ ] Dev set meets the composition minimums above; all relevant-doc references verified to exist; a manifest in `eval/retrieval-eval/sets/dev/` pins the miller + julie repo paths AND the exact commit SHAs the set was constructed against (later re-tuning must not silently drift the ground truth)
- [ ] Results/queries JSONL schemas documented in README (Task 7's integration contract)
- [ ] Sealed-set protocol documented; no sealed data in repo
- [ ] Worker-scope verification passes; diff handed to lead (parallel-lead-commit)

