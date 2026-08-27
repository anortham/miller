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

### B1 — advance the graph cache instead of rebuilding it

Give the reachability graph the same advance-on-revision shape the fact cache has, or converge it in
the leader alongside the search sidecar so readers never rebuild it. Bigger slice, separate
measurement, worth doing after A lands and the field p50 is re-read.

## Verification plan

- Guard for A1: a test that a cold-store context call performs zero `RevisionFactCache.Load` work on
  the calling thread (count assertion via the store), plus the existing promotion tests running warm
  and unchanged.
- Field proof: telemetry p50/p95 for `tool=context` over the week after release, against the
  2026-08-20→27 baseline in the audit; the `context_phase` log lines give the per-phase before/after
  on this machine.
