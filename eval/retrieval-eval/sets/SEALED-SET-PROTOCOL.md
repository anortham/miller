# Sealed retrieval acceptance set — protocol

The dev set in `dev/` is visible: it is mined from known failure modes, read during development, and tuned
against. That is exactly why it cannot certify anything. A retrieval configuration selected by repeatedly
scoring the dev set has been fitted to it, and its dev numbers stop predicting behavior on queries nobody
optimized for.

The **sealed acceptance set** exists to answer the one question the dev set no longer can: does this
configuration work on queries the implementation lane never saw?

This protocol is the secondary retrieval event. The primary offline semantic-value gate is the blinded paired
task-completion event in [`SEALED-TASK-PROTOCOL.md`](SEALED-TASK-PROTOCOL.md). Keep their artifacts separate:
retrieval queries and relevance labels never enter task-score inputs, and task prompts/checks never enter this
retrieval harness.

## Ownership

The sealed set is **owned by the user**, not by the implementation lane and not by any agent working the
semantic program.

- It lives **outside this repository**, in a location the user controls.
- Nothing in `eval/retrieval-eval/sets/` may ever contain sealed queries, sealed relevance labels, sealed
  results files, or per-query sealed reports. `DevSetTests` guards against a sealed JSONL landing here, but
  the real guard is this rule.
- No implementer reads sealed query text. Not to debug a regression, not to "just check one query", not to
  understand a surprising score. Reading it consumes it.

## Schema

Identical to the dev set — same query JSONL fields, same results JSONL contract, same `manifest.json` with
pinned repo paths and commit SHAs. See [`../README.md`](../README.md). The same binary scores both:

```bash
dotnet run --project eval/retrieval-eval -- score \
  --queries <sealed>/queries.jsonl --results <arm-results>.jsonl --out <report>.json --k 10
```

Because the schema matches, an arm needs no sealed-specific code path — it reads a query file and writes a
results file, unaware of which set it is running.

## Composition

The sealed set should mirror the dev set's shape (paraphrase clusters, identifier non-inferiority queries,
short-token, negation/ambiguous, negatives) so the two are comparable, and must satisfy the design's
leave-one-repo-out requirement: **at least one repo appears only in the sealed set** and is never used for
selection. `validate` accepts it unchanged; run it before sealing to confirm the schema and floors.

## Handoff procedure

Evaluation is an **event**, not a loop.

1. **Freeze.** The implementation lane freezes a candidate configuration — model, dims, quantization, fusion
   weights, thresholds — and records its exact identifiers. No further tuning after this point.
2. **Request.** The lane asks the user to run an acceptance evaluation, supplying the frozen arm and the
   commit SHAs its dev results were produced at.
3. **Run.** The user (or a process the user controls) runs the frozen arm over the sealed queries and scores
   the results with this harness. Each query's `search_mode` is frozen before either arm runs and both arms use
   that same mode. The lane does not see the query file, the results file, or the per-query rows of the report.
4. **Return.** The user returns **aggregates only**: `overall`, `language_macro_average`, `worst_language`,
   `per_query_class` (including the identifier non-inferiority block), `intent_cluster_summary`, and
   `negatives`. Per-query rows, cluster names, and any query text stay sealed.
5. **Decide.** Pass or fail against the gates in design §9. A failure is diagnosed on the dev set and on new
   material — never by inspecting sealed queries.
6. **Log.** Record the date, the frozen configuration, the pinned SHAs, and the returned aggregates.

## Burn rules

- A sealed set is **spent** for a given configuration once its aggregates are known. Re-scoring tweak after
  tweak against the same sealed set turns it into a second dev set, silently.
- Budget: at most a small number of acceptance events per phase. Fixing a gate failure means new dev material
  or a new sealed slice, not another pull on the same one.
- If sealed content leaks into the repo, a log, a transcript, a report, or a prompt, treat that slice as
  burned and retire it. Note the retirement in the acceptance log; do not quietly keep using it.
