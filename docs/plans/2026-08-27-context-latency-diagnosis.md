# Context tool latency — diagnosis and fix options

Date: 2026-08-27. Follow-up to Priority 2 of
[`../findings/2026-08-27-telemetry-audit.md`](../findings/2026-08-27-telemetry-audit.md). Field numbers:
context p50 ~7 s, p95 20–37 s over the last 7 days. This doc replaces the audit's first guess
("route lookups through session_projection") with a measured cost model.

## Method

Live resident MCP server on this repo (1.24.1+8e4d373091af, 1,785 files), same query repeated, phase
splits from the `Context phase {ContextPhase}` INF lines in the shared log plus the telemetry
`read_*` metadata. Three states measured:

| State | Total | Dominant phases |
|---|---:|---|
| First reference-touching call of the server process | 7,633 ms | anchor_resolution **4,813**, graph_reach 1,388, source_rescue 456, query_retrieval 345 |
| First call after a one-file revision advance | 2,988 ms | graph_reach **1,284**, anchor_resolution 544, source_rescue 377, query_retrieval 299, term_retrieval 245, semantic 206 |
| Fully warm repeat | 1,047 ms | source_rescue 367, term_retrieval 187, anchor_resolution 177, semantic 115, graph_reach 87 |

The field p50 (~7 s) matches the cold profile, not the warm one, because `context` is by design the
first call in an unfamiliar area and because any file save re-colds the revision-keyed caches. The
per-lookup sidecar cost the audit blamed is real but secondary: 2,305 lookups cost 739 ms warm
(0.32 ms each) and are spread across the retrieval phases.

## The three costs, largest first

### Cost A — the whole-generation fact-cache load blocks anchor_resolution (~4.8 s here, scales with repo size)

`PromoteTermRescueTestSubjects` (ContextTool.cs:1770) runs on EVERY query-driven context call — even
under the default `reference_mode=off` — and calls `readOutgoingMany` →
`ReferenceEvidenceReader.ReadOutgoingMany` → `QueryTimeResolutionReader`, whose construction needs
the `RevisionFactCache` for the pinned identity. The process-wide `RevisionFactCacheStore` advances
incrementally across revisions (`previous.Advance`), but the FIRST reference-touching call of the
server process — or any identity change where `CanAdvance` is false — pays the full
`RevisionFactCache.Load` (~5 s on this 1,785-file repo, measured before as the one-shot CLI's cold
cost). The load exists to answer outgoing exact references for at most `TermRescuePromotionReadLimit`
promoted test symbols — a bounded read spent on a whole-generation load.

`wait_reason=index_load` on nearly every field context row is this plus Cost B.

### Cost B — graph_reach rebuilds per revision advance (~1.3 s here)

The family-store graph reachability index is cached keyed on the snapshot; a revision advance
changes the key and the next call rebuilds it (1,284–1,388 ms cold vs 87–99 ms warm). Under active
editing every save re-colds it, so most real calls pay it.

### Cost C — retrieval phases roughly double when cold (~1 s spread)

source_rescue / query_retrieval / term_retrieval / semantic_seeds each roughly double on a cold
revision (SQLite page cache, sidecar reopen, broker warm-up). Real but smaller, and partly OS-level;
revisit after A and B.

## Fix options

### A1 (recommended — SHIPPED 2026-08-27) — never block promotion on a cold fact cache; load in the background

**Implemented and measured the same day.** `RevisionFactCacheStore.IsWarm` probes the shared store
without loading; `FamilyStoreReadSession.ResolutionFactsWarm` / `WarmResolutionFactsInBackground`
expose it through `WorkspaceReadHandle`; the ContextTool `ReferenceMode.Off` path skips promotion
and kicks the background load when cold, stamping `term_rescue: skipped_cold_facts`. Proof on a
fresh server process (fully cold, same query as the baseline): anchor_resolution **4,813 ms →
6 ms**; the background load completed before the second call, which promoted warm (404 ms,
refinement restored). Cold-call total 7,633 → 5,666 ms; the remainder is Cost B (graph_reach
3,978 ms in a fresh process), which is the next slice.

Post-review hardening (same day, from the Codex review of the shipped slice): the background warm is
store-owned and single-flight per scope (`RevisionFactCacheStore.WarmInBackground`) — concurrent cold
calls share one task instead of each parking a thread-pool worker, a faulted warm clears itself for
retry, and the ContextTool only probes/warms when promotion could actually run (non-blank query
without test/def intent). The `IsWarm` probe is advisory by design: a swap between probe and read
costs one blocking load — the pre-A1 behavior — never a wrong answer.

In the resident server, term-rescue promotion asks the `RevisionFactCacheStore` whether it ALREADY
holds (or can cheaply advance to) the pinned identity:

- Hit/advanceable → exactly today's behavior.
- Cold → SKIP promotion for this call, stamp `term_rescue: skipped_cold_facts` in telemetry, and
  kick the load in the background so the next call is warm.

Cost: a cold call's ranking may lack test-subject promotions (a ranking refinement, not
correctness); the skip is visible in telemetry. Gain: removes the whole ~5 s load from the critical
path. Small blast radius: the decision sits at the one `readOutgoingMany` call site.

### A2 — bounded fact read for the promotion set

Use the `RevisionFactCache.LoadBounded` shape (built for the one-shot CLI, byte-identical output by
contract) to answer outgoing refs for the ≤N promoted tests without the whole load. Output stays
identical even when cold. But CLAUDE.md makes bounded mode "requested by name, never inferred" and
one-shot-CLI-only for a reason — resident processes deliberately keep the full load. Extending
bounded mode into the resident context path relaxes that rule and needs the follow-on question
answered: does the full load then ever happen, or does trace/impact still pay it on their first call?

### B1 — SHIPPED 2026-08-27: the per-advance graph cost was one unbounded SQL scan, not the cache shape

Statement-level measurement (`Graph statement phase` log lines) pinned the post-advance
graph_reach almost entirely on the SUPPLEMENTAL edge reload: **1,192 ms for 25 edges**, unchanged
across runs. Root cause: `BlazorComponentGraphReader.ReadEvidence` selected every class/import
symbol with metadata from the session's `symbols` view — and the plan is a full `SCAN` of the
family store's 908k-row symbols table (8.1 GB file), because no symbols index leads on the columns
that query filters by, and the visibility join alone cannot drive one. 61k rows then streamed into
managed JSON parsing to find ~70 razor rows.

Fix: bound the query to razor files via
`file_id IN (SELECT file_id FROM files WHERE language='razor' OR path LIKE '%.razor')` — the IN
over version ids lets SQLite probe the version_id-leading index instead of scanning (0.25 ms for
the same rows, measured), plus a razor-type LIKE prefilter with the TestLinkageReader-style
escaped-spelling arm, and a `files`-exists guard for minimal legacy DBs. Proof on the same
3-call probe: supplemental **1,192 → 135 ms**; post-advance graph_reach **1,285 → 223 ms**;
post-advance call total **3.1 s → 2.0 s**; cold first call 7.6 s (pre-A1) → 4.8 s.

Remaining cold-call cost: graph resolution joins the A1 background fact load (~2.9 s inside
graph_reach on a fresh process only). Overlapping or advance-shaping that load further is the next
candidate slice if the field p50 warrants it after these two fixes are read back from telemetry.

## Post-fix eval pass (2026-08-27, same machine, current build)

Fresh-server probe of the other read tools — cold / warm / after a one-file revision advance:

| Tool | cold | warm | post-advance |
|---|---:|---:|---:|
| search symbol | 141 ms | 46 ms | 60 ms |
| search source | 79 ms | 47 ms | 57 ms |
| inspect overview | 3,700 ms | 245 ms | 482 ms |
| inspect full | 251 ms | 235 ms | 250 ms |
| trace refs | 154 ms | 142 ms | 138 ms |
| impact target | 1,464 ms | 946 ms | 1,124 ms |

The 3.7 s cold inspect is the once-per-process fact-cache load — legitimate there (references ARE
inspect's answer), and any earlier context call's A1 background warm absorbs it. Field telemetry
ranked by total week wall-time puts `inspect full` first on volume (5,535 calls, p50 215 ms — per
call healthy) and `impact git_diff` second (102 calls, p50 3.8 s, p95 22.5 s). The impact tail
attributes to `wait_reason`: `workspace_refresh` p50 7.6 s (waiting for the dirty tree to index —
the freshness contract, not waste) and `index_load` p50 3 s (the same once-per-identity cache
loads). Probe confirms: impact git_diff right after a save waits ~10 s for converge, then answers
in ~0.6 s once fresh.

Follow-up candidate (recorded, not scheduled): trigger the A1 background fact-cache warm from the
LEADER's converge completion instead of only the first context call, so every revision advance
re-warms while the agent reads the diff. Would shave the `index_load` arm of inspect-full p95 and
impact. The store advance is incremental, so the per-save cost is bounded.

## Verification plan

- Guard for A1: a test that a cold-store context call performs zero `RevisionFactCache.Load` work on
  the calling thread (count assertion via the store), plus the existing promotion tests running warm
  and unchanged.
- Field proof: telemetry p50/p95 for `tool=context` over the week after release, against the
  2026-08-20→27 baseline in the audit; the `context_phase` log lines give the per-phase before/after
  on this machine.
