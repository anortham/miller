# Miller/Julie visible agent-efficiency baseline — 2026-07-22

> Historical evidence. The current 15-task takeover-v1 calibration is
> [`2026-07-23-miller-julie-takeover-v1-visible-calibration.md`](2026-07-23-miller-julie-takeover-v1-visible-calibration.md).

**Decision: freeze the Miller candidate after the one permitted repair.** The repair corrected Miller's rendered-context budget defect, but the full paired rerun failed correctness and made efficiency unmeasurable. The semantic model was not the cause of either visible concept-search loss, so BGE-small remains the production model.

## Frozen execution

- Twelve visible task pairs covered two tasks each for exact lookup, concept search, docs/config, context assembly, references/trace, and impact/tests across five clean source snapshots.
- Both runs used Codex `gpt-5.6-sol`, medium reasoning, seed `731`, `tiktoken 0.13.0` with `o200k_base`, at most eight calls, 12,000 cumulative tool-output tokens, and 120 seconds.
- Both runs completed with zero harness voids. The only reruns were the predeclared three repetitions for the post-repair `dev-006` one-arm disagreement.
- Julie was `7.16.0` at `27d39714339778b18f412c6a5f1110de1257dcd3`, using production CodeRankEmbed on MPS.
- The baseline used Miller `1.13.0+072f1f1779e0`; the visible candidate used `1.13.0+1f4724f11ca9`, production BGE-small, and code-artifact SHA-256 `7a75751f8264eab64d79a1f8d275e0900145ae4648ea81a3214717994560c065`.
- The committed identity manifests contain every product, snapshot, model, tool, schema, tokenizer, and environment hash. The baseline manifest's Miller binary field identified the stable apphost; the candidate harness correction additionally hashes the actual managed code artifact.

## Paired gates

| Gate | Baseline | Frozen candidate |
| --- | --- | --- |
| completion cells | both 1; Miller-only 0; Julie-only 0; neither 11 | both 0; Miller-only 0; Julie-only 1; neither 11 |
| correctness floor | pass: 1 versus 1, zero critical losses | fail: 0 versus 1, zero critical losses |
| token route | fail: 2,804 versus 278 median tokens | unmeasurable: zero both-pass tasks |
| call route | fail: 7 versus 2 median calls and higher Miller tokens | unmeasurable: zero both-pass tasks |
| p75 wall guard | fail: 27,581 ms versus 22,536 ms (`1.224x`) | unmeasurable |
| aggregate verdict | fail | fail |

Baseline row failures were Miller `7 budget_exceeded / 4 incorrect` and Julie `3 budget_exceeded / 3 incorrect / 5 product_error`. Candidate row failures were Miller `9 budget_exceeded / 4 incorrect` and Julie `4 budget_exceeded / 4 incorrect / 3 product_error`; these counts include the three stabilized repetitions for `dev-006`.

## Miller loss evidence

| Task | Class | Baseline trajectory | Candidate trajectory |
| --- | --- | --- | --- |
| dev-001 | guidance | six calls, 6,048 tokens, extra helper evidence, incorrect | bounded context, five more full inspections, 5,245 tokens, incorrect |
| dev-002 | routing then guidance | six broad searches before exact inspection, 10,573 tokens, incorrect | exact factory in two calls, but extra unaccepted evidence, incorrect |
| dev-003 | output size then guidance | 12,146 tokens | target rank 1, then eight-call exhaustion |
| dev-004 | output size then guidance | 12,168 tokens | target rank 1, then eight-call exhaustion |
| dev-005 | routing | six searches missed authoritative config | config reached only on call eight, 12,349 tokens |
| dev-006 | routing | completed in seven calls | two of three repetitions exhausted eight calls; one completed |
| dev-007 | output size then guidance | three calls produced 13,990 tokens | bounded context followed by seven calls, call-budget failure |
| dev-008 | guidance then output size | exhausted eight calls after context | full-file reads produced 26,334 tokens |
| dev-009 | ambiguity | eight inspections did not isolate required references | eight calls including refs trace still did not converge |
| dev-010 | guidance | correct caller found; extra evidence failed strict verifier | same evidence-precision failure in four calls |
| dev-011 | routing then output size | broad search exhausted eight calls | two broad searches produced 18,140 tokens |
| dev-012 | routing then guidance | eight searches missed focused tests | tests found in three calls; extra evidence failed strict verifier |

Each run-specific loss has exactly one class in [`miller-loss-classification.json`](agent-efficiency/2026-07-22-visible/miller-loss-classification.json). The repeated evidence-precision failures are real under the frozen strict verifier: a materially correct answer can still fail when it asserts citations outside the accepted task anchors.

## One repair and semantic disposition

The baseline exposed context responses of 16,228, 34,279, 47,876, and 37,154 characters despite requested budgets of 3,000-4,000 estimated tokens. The one permitted repair now bounds complete compact and JSON rendering. Direct replays ended at `2,973/3,000`, `3,987/4,000`, `3,972/4,000`, and `3,991/4,000` estimated tokens while preserving order and fitting responses unchanged.

The identical-corpus diagnostic used the same 22 normalized excerpts, boundaries, queries, and candidate count. Both BGE-small and CodeRankEmbed ranked the `dev-003` and `dev-004` targets first. BGE-small was also much faster: 498 ms versus 10,335 ms startup, 109 ms versus 635 ms warm batch, and 7-8 ms versus 100-106 ms per query. A CodeRankEmbed swap would not address the observed agent failures and is rejected.

## Freeze consequence

No second visible repair, task edit, semantic-canary edit, or model swap is permitted. The visible replay remains frozen at `1f4724f11ca97aa46388702fd4782c23738d7682`. Single-pass pre-merge review then produced a byte-equivalent render-work fix at `1445282edb89253eb43106570142b4173e158bd0`; that reviewed commit, version `1.13.0+1445282edb89`, and code-artifact SHA-256 `43d28783e217357611e58f30f957f067fe27443b2dcc1b33bbe5ed37f1ab643d` are the candidate identity for the user-controlled 30-pair sealed run. Under the predeclared decision rule, any sealed correctness or efficiency failure keeps Julie primary immediately; only a sealed aggregate proving both products share the same architectural limit can justify a separate new-project design.

The review fix did not trigger another visible agent replay because it does not alter ranking, selected priority prefix, or response bytes for any budget at or above the canonical empty envelope. It replaces linear tail removal with binary search for the same largest fitting prefix. A caller-level 668-item regression proved identical retained JSON while reducing measured allocation from 1,626,385,624 bytes to below 32 MB. Budgets smaller than a valid empty envelope now take an explicit deterministic fast path and return that same canonical envelope.

## External review

- Reviewer: Claude Code 2.1.218, ephemeral adversarial read-only run over the full branch diff.
- Returned: 2 findings; fixed: 2 in `1445282e`; dismissed: 0; flagged: 0.
- Medium: quadratic context trimming — verified real and fixed with logarithmic prefix search.
- Low: tiny positive budgets cannot contain a valid empty envelope — verified real improvement, documented, and covered in all four ordinary/reference-aware compact/JSON cases.
- The offline harness was also verified absent from Miller project content and release packaging.
- Cost: 6 direct input tokens plus 245,342 cache-creation and 475,347 cache-read tokens; 9,444 output tokens; `$2.93`.

The reproducible scorer exports and manifests are under [`docs/findings/agent-efficiency/2026-07-22-visible/`](agent-efficiency/2026-07-22-visible/).

## Verification ledger

The visible replay ran at `1f4724f11ca97aa46388702fd4782c23738d7682`. The post-review code gates below ran at reviewed candidate `1445282edb89253eb43106570142b4173e158bd0` plus this evidence update on 2026-07-22 UTC.

| Scope | Command | Result |
| --- | --- | --- |
| benchmark contracts | pinned Python `unittest` modules | pass: 55 tests |
| evaluator | `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj -c Release --no-restore` | pass: 81 tests |
| dependency boundary | no `PackageReference` or `ProjectReference` in `RetrievalEval.csproj` | pass |
| Release build | `dotnet build Miller.slnx -c Release --no-restore` | pass: 0 warnings, 0 errors |
| focused review fix | `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ContextToolTests` | pass: 41 tests |
| fast suite | `scripts/test.sh` | pass: 4,562 passed, 2 expected skips, 18 seconds |
| Scale suite | `scripts/test.sh scale` | pass: exit 0 with the real pinned extractor path |
| evidence integrity | SHA-256 check of every artifact listed by both evidence manifests | pass |
| repository hygiene | JSON parse plus `git diff --check` | pass |
| visible replay | frozen 12-pair baseline and candidate run plus exact `agent-score` command | pass: zero harness voids; candidate verdict intentionally fail |
