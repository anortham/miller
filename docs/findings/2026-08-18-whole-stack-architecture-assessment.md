# Whole-stack architecture assessment — why the stack is still "not fast"

Date: 2026-08-18. Scope: Miller 1.19.6, julie-extract 2.33.7, julie-semantic-sidecar, live Linux
dogfood plus the committed evidence from the 2026-08 performance campaign. Question asked: after two
weeks of perf work, is the stack fundamentally doing something wrong at a higher level?

**Answer: yes. One architectural decision produces almost all of the remaining cost: the stack
materializes a workspace-global reference-resolution graph inside a versioned, multi-view store, and
keeps that graph current on every save. Everything else in the stack is already fast.**

## 1. What is fast (leave these alone)

Measured on this machine (miller repo, ~30 MB tracked source, ~1,750 files) and in committed docs:

| Operation | Measured | Source |
|---|---:|---|
| Cold-process CLI symbol search | 0.22–0.24 s | live, 4 warm runs |
| Cold-process natural-language search | 0.40–0.43 s | live |
| Cold-process inspect | 0.19–0.20 s | live |
| No-change refresh | 0.3–0.4 s | live |
| Extraction (tree-sitter, rayon) | ~3.4 ms/file; 6.3 s cold for 1,018 files; 51 ms no-change rescan | julie ph0/baselines |
| Legacy standalone-artifact one-file delta | **51 ms** | julie finding 2026-08-05 |
| Semantic sidecar converge | sub-second per delta; never blocks reads | live logs |

The agent-facing value is also proven: 2.2× correct tasks vs a bare agent, 0% vs 27% wrong actions
(`2026-07-29-miller-vs-bare-agent-calibration.md`). The product is right. The maintain pipeline is
what is wrong.

## 2. What is slow (all one subsystem)

From the Aug 16–17 Linux logs on this small repo, and the committed findings:

| Operation | Measured | Budget |
|---|---:|---:|
| `resolve` phase per change batch | p50 12 s, p95 60–77 s, max 250–600 s; **117 min total in one day** | 5 s one-file / 60 s full |
| Leader startup on the big store | 27–28 s with `DidWork=false`; live p50 87–108 s, max 228 s | 2 s |
| One-file production resolve (frozen store) | 178.6 s after all fixes | 5 s |
| Import phase | p95 27 s, max 166 s; 71–85 s on a 1,628-file tree | — |
| Sidecar converge after one save | 21 s to searchable (pre-fix; post-fix never re-measured) | — |
| Search-sidecar FTS5 full build | 160–220 s, dominates a 457 s worktree open | — |
| Cold full converge | 7–9 min (226k symbols); 457 s dotnet/runtime post-rebind | — |

Write amplification on this repo: 30 MB source → **~6+ GB derived** (store.db 3.1 GB live pages of
which half is B-tree indexes, 14 resolution-base snapshots ≈ 1.5 GB, triplicate keyed search/content
sidecars ≈ 1.2 GB, legacy local symbols.db 990 MB). Every revision writes a full manifest copy
(449 manifests × 1,753 files = 702 k rows). A 3.4 GB WAL was observed beside a 1.8 GB store.db on
Windows. Side note: `~/.miller` holds 342 GB, ~330 GB of it perf-test scratch (`task8*`,
`perf-recovery-*`) that can be reclaimed.

## 3. Why resolution cannot be made fast in this design

Three facts, all from this program's own measurements, compose into the conclusion:

1. **The correctness rule is global.** Tier 4 binds a name when there is exactly one
   kind-compatible candidate in the whole workspace. One added symbol anywhere can flip bindings
   anywhere. There is no correct file-local answer, so every "scoped" resolve is a conservative
   over-approximation.
2. **The over-approximation collapses to the whole repo.** The ph0 cost curve
   (`spike/index-store-ph0/resolution-growth/results.md`) shows a one-file C# change re-derives
   **74.5% of the corpus**; 5 files → 92%. The program doc concedes it: "the bound bites at file
   one, not at the crossover." The scope planner itself does 4+ full passes over the identifier
   table before any resolution work starts.
3. **The reuse the versioning was built for does not pay.** Ph0 measured that binding a stored base
   to a sibling branch tip is **32.4% slower than throwing the base away and rebuilding the tip**.
   The storage half pays (8 views ≈ 1.027× bytes); the compute half is negative. The delta path
   pays ~3× per row (20 k rows/s) vs the bulk path (71 k rows/s).

The two-week campaign (109 store commits, six releases) was honest and effective — 520 s → 50 s on
the amplification workload, 226 s saves → scoped 14 files — but it is dividing a constant while the
curve's shape is fixed. The bases/deltas/rebases/proofs/scope-journal/crossover machinery, the
stranded claims, the WAL blowups, the coordinator quantum, and most of the Windows pain exist to
keep this materialized global graph current. That is where the two weeks went.

## 4. The higher-level change: stop materializing resolution

**Store facts. Resolve at query time.**

Keep exactly what extraction already produces fast and file-locally: symbols, identifiers,
reference sites, imports, type facts, structural facts — all keyed by content hash. Delete the
persisted resolution layer. When a read tool asks "who references X" / "what does this bind to",
run the same tier rules (same-file → import-guided → receiver-typed → unique-global) **for that one
name, at query time**, against two small indexes:

- name → definition candidates (with kind/language) — the tier-4 uniqueness check becomes one
  indexed count.
- name → reference sites (per file) — the fan-out is exactly the result the caller asked for.

Why this wins:

- A query touches one name (or a handful). Milliseconds against an indexed 350 k-symbol store —
  Miller's read path already proves this class of latency (0.2 s cold-process including .NET
  startup).
- Freshness collapses to "extraction is current", which costs 51 ms–6 s, not 5–600 s. The resolve
  phase, resolve subprocess, resolve claims, and resolve backoff all cease to exist.
- Ubiquitous names (`Scan`, `Assert`) stop hurting *every writer on every save* and instead cost
  only the caller who asks about them — bounded, ranked, and cacheable per revision.
- The evidence-gated pattern Miller already uses (`references candidates`, confidence tiers) fits
  query-time answers naturally.

What changes semantically:

- Whole-graph products (dead-code candidates, `dead_code_candidate_count`, some impact rollups)
  become a batch job over the fact store — same answers, computed like `metrics` already is,
  instead of being a freshness gate on every save.
- Hot-symbol fan-out queries get a per-revision cache; an optional background precompute for
  popular names is an *optimization*, never a correctness gate.

## 5. Second change: shrink the store to a content-addressed fact cache

The family store bought cross-worktree sharing, and its own measurements say only the *bytes*
sharing pays. A content-addressed cache — per-file extraction facts keyed by
`blake3(content)` — gives the same reuse with none of the machinery:

- No coordinator, no leases, no 4 s quantum, no fencing, no generations, no `CURRENT`, no
  cross-process protocol, no stranded `claimed` rows. Multi-worktree agents share cache hits by
  construction (a branch switch is ~all cache hits).
- Each worktree assembles its own compact serving index from cached facts. The bulk path runs at
  71 k rows/s — the 1,420-file corpus assembles in ~5 s, and per-file cache hits make it far less.
- A "manifest" is just the current (path, hash) list — no more full manifest copy per revision, no
  702 k-row manifest table, no O(repo) temp-table projection per read-session open.

This is close to the pre-store paradigm (51 ms one-file deltas) plus the one thing the store was
actually needed for: sharing extraction work across worktrees.

## 6. If the big change is too much right now — highest-leverage fixes in place

Ranked by measured cost removed:

1. **Persist a name → file inverted index** in store.db so scope planning is a lookup, not 4 full
   identifier-table scans with `json_extract` per row (julie `delta_scope.rs`). This is the
   in-architecture version of §4 and the single biggest resolve win.
2. **Cache the Miller read session per index identity.** `FamilyStoreReadSession.Open` does ≥3 full
   manifest passes plus a whole-repo temp-table copy and two temp index builds — per tool call, per
   freshness swap, per sidecar converge. This is the likely source of the shared 0.7–0.8 s tails
   on every warm tool.
3. **Fix the O(N²) import quantum**: `validated_payload` re-parses the whole file-list JSON and
   re-plans all chunks on every 8-file quantum (julie `executor.rs`). Plausibly a large share of
   the 71–85 s import.
4. **Batch the incremental delta**: Miller spawns one julie-extract subprocess per changed file,
   serially, under the ops gate (`ApplyIncrementalFileDelta`). 200 reconciled files = 201 spawns.
5. **Widen the sidecar delta path**: any generation flip or L1→L3 level change drops search/content
   sidecars to a full FTS rebuild (the 160–220 s item). The stamp is too coarse.
6. **Raise the consumer resolve deadline**: julie's default request timeout is 30 s against
   resolves measured at 55–180 s, so concurrent consumers time out instead of waiting.
7. **Reclaim ~330 GB** of `~/.miller` perf scratch (needs approval; nothing deleted yet).

## 7. Suggested validation spike (before committing to §4/§5)

Per this program's own gate culture: no adoption without numbers.

- Prototype query-time resolution as a read-only consumer of the *existing* store.db facts
  (identifiers, reference_sites, imports, type_facts). No producer changes.
- Gate A: `refs`/`trace` parity against the materialized edges on the current exact view
  (sample of symbols across languages, including ubiquitous names).
- Gate B: warm query latency ≤ 500 ms p95 on the Miller store; ≤ 2 s on a dotnet/runtime-scale
  store, including the worst fan-out names.
- Gate C: end-to-end save-to-correct-answer ≤ 5 s (extraction + sidecar delta only, no resolve).
- If all three pass, the resolution layer and its coordinator surface can be retired
  incrementally: serve reads from query-time resolution first, keep the batch graph only for
  dead-code/metrics products.

Estimate in agent terms: the spike is 1–2 focused sessions (read-only prototype + parity/latency
harness). The full §4+§5 migration is a multi-session program across both repos, but it *deletes*
far more machinery than it adds.
