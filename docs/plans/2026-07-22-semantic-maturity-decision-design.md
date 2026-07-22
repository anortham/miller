# Semantic Maturity Decision Program

**Date:** 2026-07-22
**Status:** Accepted direction (user approved 2026-07-22); implementation plans required before code
**Architecture risk:** High — this extends a frozen privacy/statistical contract and creates the evidence used
to keep or remove a costly product subsystem.

**Timing update:** The
[Miller/Julie agent-efficiency decision](2026-07-22-miller-julie-agent-efficiency-decision-design.md) now owns
the immediate product-direction verdict, due no later than 2026-07-25. This program continues as background
evidence for Miller's semantic-runtime promotion or removal; its day-14/day-30 window no longer delays choosing
Miller or Julie as the primary product. The agent-efficiency design's one visible-set Miller repair is the only
exception to this program's tuning hold and must not alter the immutable canary binary or cohort identity.

## 1. Decision

Do not promote semantic retrieval and do not remove it yet. Run one bounded, pre-registered decision program
that can produce a credible keep/remove verdict:

1. Freeze one exact Miller build and semantic identity.
2. Run a canary contract v3 decision profile for 14 days, with a hard stop at 30 days.
3. Aggregate privacy-safe evidence across machines without pooling incompatible cohorts or double-counting
   overlapping exports.
4. Run a user-owned, blinded, paired task-completion evaluation. Retrieval metrics remain diagnostic.
5. Promote only if value, safety, latency, reliability, and operating-cost gates all pass.
6. If a powered gate fails, or the program remains too underpowered to demonstrate useful demand by day 30,
   remove the complete semantic runtime and artifact tax rather than leaving a dormant subsystem.

No ranking tuning or additional semantic tool integration happens before the decision instrumentation and
sealed evaluation are usable. The current evidence is promising but cannot support either promotion or
removal:

| Evidence | Current result | Meaning |
| --- | --- | --- |
| Visible dev replay | production recall@10 `0.6267` vs lexical `0.5122`; nDCG@10 `0.5834` vs `0.4748` | Semantic has measurable retrieval value on the tuned/visible set. |
| Explicit lexical-zero probes | 2 useful rescues | The new capability is real, but two probes are not a population result. |
| Current v2 causal cohort | 0/30 control units, 0/30 treatment units | No production effect estimate. |
| Current v2 warm latency | 6/100 warm treatment rows, 0/100 control rows | No authoritative warm-latency verdict. |
| Current v2 identifier shadow | 0/30 units | Ten-percent sampling is too slow for this decision window. |
| Cold one-shot p95 | production `1097.7 ms` vs lexical `202.9 ms` | CLI cold-start cost is large; it is not the warm-server gate. |
| Sidecar memory | about 194 MiB steady in the observed process; about 486–491 MiB peak in prior converges | Cost is material and must be justified. |
| Markdown / negative slices | markdown zero for every arm; negative FPR 1.0 for every arm | These are evaluation/search gaps shared by lexical and semantic, not evidence of a semantic regression. |

## 2. Scope and boundaries

### In scope

- A versioned canary v3 decision profile with 100% identifier-unit shadow sampling.
- Explicit v2/v3 selection for local export and local gate commands.
- A privacy-safe multi-export combiner in the existing CLI.
- A pure paired task-completion scorer and sealed-task protocol in `eval/retrieval-eval`.
- Root-cause disposition of the all-arm markdown-zero and negative-query results before sealed acceptance.
- A reproducible frozen-build install/runbook and a final maturity decision record.
- Telemetry-derived value, safety, latency, fallback, rescue, and semantic-participation evidence.

### Out of scope

- A new MCP tool. Existing search behavior and CLI/offline surfaces are sufficient.
- Fleet semantic ranking, embeddings-as-a-service, guidance/confidence views, or any other Eros-owned surface.
- A machine-wide singleton semantic service. That architecture remains separately deferred.
- Arbitrary operator-controlled sampling percentages or gate thresholds.
- New semantic integrations in `context`, `impact`, `inspect`, `trace`, patterns, or reports before the verdict.
- Shipping, publishing, pushing, or changing the normal semantic default.
- The actual sealed tasks, prompts, acceptance checks, per-task outputs, or arm identities. They remain outside
  this repository under user control.

## 3. Architecture quality gate

### Affected modules and public interfaces

| Area | Responsibility | Caller-facing change |
| --- | --- | --- |
| `CanaryContractProfile` | Own contract version and identifier shadow sampling policy for each activation mode. | `MILLER_SEMANTIC_CANARY=decision` selects v3; `off`, `on`, `0`, and `1` retain their current meaning. |
| `CanaryTelemetry` / `SearchTool` | Stamp the selected contract and use its sampling policy without changing served identifier results. | V3 identifier units shadow at 100%; v2 remains 10%. |
| `CanaryExport` / `CanaryGateReport` | Read one explicit contract version; keep the v2 rendering byte-identical. | `miller telemetry canary --contract 2|3`; default is 2 for compatibility. |
| `CanaryAggregate` | Validate, deduplicate, merge, and score privacy-safe v3 exports only. | `miller telemetry canary combine <export.json>... [--json]`. |
| `retrieval-eval` task scorer | Score paired baseline/candidate outcomes without needing task text or acceptance criteria. | `retrieval-eval task-score --tasks ... --baseline ... --candidate ... --out ...`. |

### Depth and locality

- Contract policy is resolved once by `CanaryContractProfile`; search and telemetry code consume the profile
  instead of scattering `mode == decision` checks.
- All aggregate validation, unit merging, statistical math, and JSON rendering live in `CanaryAggregate`.
  `CliDispatch` remains argument routing only.
- Task scoring is independent of retrieval scoring. It gets separate models, validation, scorer, and report
  files so task-completion policy cannot silently change recall/nDCG output.
- The local raw-row gate remains authoritative for millisecond latency. The export combiner can exactly
  reconstruct success and identifier-shadow statistics, but its bucketed latency result remains an explicit
  screen rather than a fake precise gate.

### Rejected interface lanes

- **Replace v2 in place:** rejected because v2 is frozen and already has live rows.
- **An arbitrary `MILLER_SEMANTIC_SHADOW_PERCENT`:** rejected because it fragments evidence and lets operators
  change a pre-registered population after seeing results.
- **A separate analysis executable for canary exports:** rejected because it would duplicate Miller's privacy
  schema and statistical contract and drift from the writer.
- **A new MCP analytics tool:** rejected because this is operator evidence, not an agent retrieval action.
- **Pooling versions or semantic identities:** rejected because a mixed cohort can manufacture a false pass.
- **Committing a sealed set:** rejected because implementer access burns the acceptance evidence.

## 4. Canary v3 decision profile

### Activation

`MILLER_SEMANTIC_CANARY` gains one value:

| Value | Contract | Hybrid assignment | Identifier shadow |
| --- | ---: | --- | --- |
| `off`, `0`, invalid | none | none | none |
| `on`, `1` | 2 | existing 50/50 assignment | existing 10% of identifier units |
| `decision` | 3 | the same 50/50 assignment and experiment id | 100% of identifier units |

`MILLER_SEMANTIC=off` continues to outrank every canary value: no classification, vector probe, model process,
shadow execution, or telemetry stamp. Identifier results remain lexical and byte-identical under v3; only the
discarded comparison runs more often.

V3 deliberately keeps these v2 values unchanged:

- hybrid experiment id `semantic_hybrid_search_v1`;
- identifier experiment id `semantic_identifier_noninferiority_v1`;
- assignment version `1` and 50/50 bucket boundary;
- assignment unit `(workspace_id, utc_date, query_class)`;
- semantic identity cohort tuple;
- five-call unit suppression floor and every frozen statistical threshold;
- privacy rule: no query text, path, workspace id, raw per-call latency, or per-result content leaves a machine.

Rows from v2 and v3 are never pooled. Moving to the decision build starts a clean evidence cohort.

### Export source identity and overlap safety

V3 export requires `--source-id <opaque-id>`, exactly 32 lowercase hexadecimal characters. The operator creates
one random 128-bit id per telemetry ledger and keeps it in the canary manifest. Miller does not derive it from
a hostname, username, hardware id, or filesystem path. It is present only so the combiner can detect
overlapping exports from the same source.

The v3 envelope adds:

- `schema_version: 3`;
- `canary_contract_version: 3`;
- `export_source_id`;
- `warm_total_latency_bucket_counts` on treatment units, containing only eligible rows whose
  `canary_embed_warmth` is `warm`.

The v2 envelope and rendering remain byte-identical. V3 keeps the existing stable unit id, exact semantic
identity fields, counts, enums, and histograms.

### CLI

```text
miller telemetry canary --contract 2|3 [--source-id ID] [--from YYYY-MM-DD] [--to YYYY-MM-DD]
miller telemetry canary --gate --contract 2|3 [--json]
miller telemetry canary combine <export.json>... [--json]
```

- Omitting `--contract` selects 2, preserving existing scripts.
- `--source-id` is required for a v3 export, rejected for v2, and not used by the local gate.
- `combine` accepts at least one v3 export and rejects v2, unknown schemas, unknown enum keys, invalid count
  totals, incomplete identities, malformed unit ids, and incompatible experiment/contract identities.
- Exact duplicate `(export_source_id, window)` documents are deduplicated only when their complete content is
  identical. Different content for the same source/window is an error.
- Partially overlapping windows for one source are an error. Operators combine adjacent weekly windows or one
  final full-window export per source, never both.
- Units with the same `unit_id` from different source ids are one randomized unit and are merged before
  analysis. They are not treated as independent observations.
- Human and JSON output identify excluded/suppressed counts and the reason a clause cannot pass.

### Aggregate interpretation

The combiner computes the frozen success-rate and identifier-shadow clauses exactly from exported unit counts
and histograms. It reports a bucketed warm-latency screen from `warm_total_latency_bucket_counts` and control
`total_latency_bucket_counts`; it never labels that screen the authoritative 20% gate.

A final decision requires the exact local v3 raw-row gate from the frozen cohort in addition to the combined
report. Multi-machine evidence demonstrates breadth; local raw rows provide the precise latency decision.

## 5. Frozen canary build and evidence handling

The canary runs one immutable Release build copied with its complete `.tools` payload to a versioned local
directory outside any mutable worktree. The manifest records:

- Miller semantic-decision commit and `miller version` output;
- binary SHA-256 and absolute executable path;
- sidecar version/hash, encoder fingerprint, storage schema, corpus generation, fusion profile, policy version;
- source id, start time, target day 14, hard-stop day 30;
- MCP client config locations updated to the frozen executable;
- baseline workspace status/health, vector revisions, process RSS, idle CPU, and vector database sizes.

Weekly exports use non-overlapping UTC windows, wait at least the frozen 600-second attribution horizon after a
window closes, and are archived outside the telemetry ledger. Rebuilding or changing the binary, encoder,
storage, corpus, fusion, or policy ends that cohort; it does not silently reset the clock while claiming
continuity.

The normal product default remains unchanged during the canary. Only explicitly enrolled clients use
`MILLER_SEMANTIC=on` and `MILLER_SEMANTIC_CANARY=decision`.

## 6. Blinded paired task-completion evaluation

The retrieval dev set cannot certify user value because it is visible and has already influenced the system.
The primary offline value measure is paired task completion:

- At least 30 tasks, spanning at least five repository/language-family combinations.
- Every task is run from the same clean snapshot under baseline and candidate arms, with the same agent model,
  instructions, token/time budget, tools, and acceptance checks.
- Arm labels and run order are blinded and randomized by the user-controlled coordinator.
- Task prompts, checks, raw trajectories, per-task results, and arm mapping stay outside the repository and
  outside implementation-agent context.
- The scorer receives only a task manifest and two result files. The manifest contains opaque `task_id`, repo,
  language, and query-profile labels; it contains no prompt or acceptance text.
- Each result contains `task_id`, `completed`, elapsed milliseconds, total tool calls, search calls, and
  zero-result search calls. The aggregate report contains no per-task rows.

The primary paired statistic is the candidate-win share among discordant pairs:

```text
candidate_only / (candidate_only + baseline_only)
```

Its two-sided 95% Wilson lower bound must be greater than `0.5`, with at least 30 complete pairs. Missing,
duplicate, or unknown task ids are hard validation failures. Tied completions, duration, tool calls, search
calls, and zero-result calls are reported diagnostics, not alternate ways to pass.

Identifier/path tasks are a named safety subset. They do not replace the live identifier-shadow gate; the
subset is underpowered below five complete pairs and fails safety when `baseline_only > candidate_only`.
Other subgroup results with fewer than five tasks are suppressed.

The existing sealed retrieval score remains a secondary requirement using the unchanged production arm and
frozen retrieval thresholds. The task scorer does not modify recall/nDCG logic or the visible dev set.

## 7. Markdown and negative-query disposition

The zero markdown score has a demonstrated evaluation-routing cause. All four markdown queries are
`docs_like`, but `run-live-arm.py` ignores `query_class` and invokes `miller search --json` without a mode, so
every query runs through `auto`. Product code intentionally disables auto text/semantic rescue for JSON output
(`SearchToolRescueTests.SemanticRescue_IsNeverConsultedForJsonOutput`) because the symbol-result JSON array has
no mixed content-row shape. The replay therefore never exercised the explicit semantic content route that the
agent guidance prescribes for docs/config prose.

Fix the evaluator rather than silently changing the public JSON schema:

- `EvalQuery` gains an optional `search_mode` enum (`auto|symbol|file|content|source`), default `auto` for
  backward compatibility.
- The four visible markdown/docs-like rows are explicitly pinned to `content` before replay. Sealed-set owners
  choose and freeze a mode while constructing the set; implementers never infer a sealed mode after seeing a
  score.
- `run-live-arm.py` passes the frozen mode to Miller. The report records mode composition so a score cannot hide
  that one arm or one run used different routing.
- If markdown is still zero through the correct content route, diagnose corpus coverage, result conversion,
  relevance drift, and retrieval in that order. A genuine unsupported-language result blocks broad default-on.

The negative FPR is also explained by the current product contract: Miller returns ranked candidates and has
no confidence-based abstention promise. The runner correctly records those shown candidates, so 1.0 FPR is a
diagnostic rather than a semantic regression. It is not a promotion gate for this program. Do not add or tune a
score threshold after reading sealed results. If abstention becomes a product requirement, approve it as a
separate behavior change, tune it on visible data, freeze it, and spend a new sealed slice.

Because both failures affect lexical and semantic today, neither is evidence for removing semantic by itself.
Correct-mode markdown coverage remains a maturity gate; negative behavior remains report-only.

## 8. Pre-registered promotion and removal decision

### Promotion requires every item

- The exact frozen v3 cohort passes the existing local success-rate, warm-latency, and identifier-shadow gates.
- The combined privacy-safe report passes success and identifier shadow across multiple sources, repositories,
  and language families; its latency screen shows no possible regression.
- The blinded task-completion gate passes, with no identifier/path safety reversal.
- The unchanged sealed retrieval set passes its frozen overall, worst-language, identifier, and composition
  checks.
- Markdown has non-zero valid coverage, or a narrower product scope has been explicitly approved before the
  sealed run.
- Fallbacks other than `none` are at most 2% of eligible treatment calls after the first 24 hours; no semantic
  failure converts a lexical success into an error.
- On the pinned BGE lane: steady ready-state sidecar RSS is at most 256 MiB, converge peak is at most 600 MiB,
  three concurrently active Miller hosts total at most 768 MiB of sidecar RSS, and five-minute ready-state idle
  CPU averages below 1% per sidecar.
- A Miller-scale corpus of at most 15,000 vector units converges in at most 60 seconds on the reference machine,
  and semantic-off still performs zero semantic work.

Passing promotes only the current lexical-first production policy: identifiers and paths remain lexical-first;
semantic participates for prose/docs/mixed and weak or empty lexical evidence. Further tool integrations or a
different model require their own measured plan.

### Removal triggers

- Any powered causal, task-completion, identifier-safety, or warm-latency gate fails.
- A required reliability or operating-cost gate cannot be met without a separately approved service-sharing or
  runtime redesign.
- Day 30 arrives without enough eligible use to pass the minimum populations. Insufficient demand after an
  intentionally accelerated decision profile means the ongoing dependency and maintenance tax is not justified.
- The sealed event fails and a second pre-approved sealed slice does not pass after fixing a bug demonstrated on
  non-sealed material.

Removal means removing semantic serving, vector convergence/storage, the sidecar/model dependency, semantic
telemetry/canary plumbing, semantic CLI/config/docs, and semantic-specific tests while preserving lexical
behavior and generic telemetry history. It does not mean leaving the feature permanently off but still shipped.

## 9. Delivery slices

This design is implemented as two independently testable plans:

1. **Decision canary and aggregation:** v3 profile, version-selected export/gate, privacy-safe combiner,
   contract/runbook, and frozen local cohort setup.
2. **Sealed task evaluation and baseline validity:** paired task scorer/protocol plus markdown and negative-query
   disposition.

Only after both plans pass their branch gates is the frozen canary installed. The 14/30-day observation period
then produces a findings document with one of three honest states: promote, remove, or still underpowered before
day 30. `Still underpowered` is not allowed after day 30.
