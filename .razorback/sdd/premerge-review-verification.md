# Pre-merge codex review — lead verification record

Reviewer: codex (codex-cli 0.144.6, adversarial schema mode, read-only sandbox)
Input: full branch diff main..338e665 (58 files, +7108/−929) + commit log + plan focus
Verdict: needs-attention, 5 findings

## Classifications (all verified hands-on by lead before dispatch)

1. **HIGH — Scorer primary metrics violate design §8 cluster requirement** → real-bug.
   Verified: Scorer.cs aggregates every positive query independently; design §8 says "scored as
   clusters, not independent samples". Lead independently recomputed from recorded artifacts and
   REPRODUCED codex's numbers exactly: cluster-mean units give qwen3-1024d-int8 0.6875/0.6358 vs
   qwen3-512d-int8 0.6910/0.6370, and 512d wins worst-language 0.5989 vs 0.5327 (cluster-max
   variant agrees directionally: 0.7708/0.7220/0.6577 vs 0.7500/0.7197/0.5327). The recorded
   1024d pin rests on non-compliant scoring. Fix: fix-eval worker (rescore all arms, re-derive pin).

2. **HIGH — semantic arms rank test units the production surface excludes** → real-bug.
   Verified: design line ~225 says is_test excluded from default search recall;
   SearchTool.ResolveExcludeTests auto-hides tests for NL phrases (BM25 arm: 58 test paths/799),
   bench.py only repo-filters (semantic arms: 178 and 168 test paths/820). No golden relevant doc
   is a test path, so exclusion cannot break any query; vectors cached so re-rank is cheap.
   Fix: fix-eval worker.

3. **HIGH — canary warm-latency gate not computable from sanctioned export; estimators unfrozen**
   → real-bug. Verified: gate text requires ≤20% p95 warm-latency regression; export invariants
   forbid duration_ms in any form and expose warmth + semantic-step latency only as marginals;
   control rows carry no semantic timing at all. CI method/min-samples/non-inferiority margin not
   frozen. Fix: fix-canary worker (local raw-row gate definition + joint bucketed total-latency
   export field + frozen analysis parameters).

4. **MEDIUM — attribution misses qualified inspect targets** → real-improvement.
   Verified: contract hashes bare name + path only; TelemetryScope hashes the exact target string;
   qualified spellings (Parent.Member) never match. Fix: fix-canary worker
   (canary_result_qualified_hashes + matching-rule extension + conformance cases).

5. **MEDIUM — EditTool exception path loses request metadata** → real-bug.
   Verified: EditTool.cs stamps Op/SetTarget/request metadata only after EditService.Execute;
   catch path stamps only outcome/error/failure_reason, defeating operation-level diagnosis of
   unhandled rows. Fix: fix-edit worker (stamp request-derived facts pre-Execute + tests).

Dismissed: none. Flagged-for-human: none at dispatch time (statistical parameter values chosen by
fix-canary where design §9/§11 is silent will be surfaced as judgment calls in the morning report).

Cost note: codex does not surface per-request token counts in its JSON output.
