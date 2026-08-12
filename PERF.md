# Miller Performance Blockers

This is the live release-blocking performance ledger for Miller and its pinned `julie-extract` producer.
Do not close an item from code inspection alone. Close it only with the focused regression and one bounded real
dogfood run named in its acceptance gate. Never repeat an unchanged operation that runs longer than 60 seconds
without first adding new phase, query-count, or resource evidence.

## Product budgets

| Surface | Development budget | Constrained Windows-oriented budget |
|---|---:|---:|
| Warm `inspect` | 500 ms | 2 s |
| Warm `context`, `impact`, or `trace` | 2 s | 5 s |
| Cold family-store interactive read | 5 s | 10 s |
| Idle retained private/PSS per Miller host | 350 MB | 350 MB |
| Peak private/PSS per ordinary read host | 600 MB | 600 MB |
| One-file incremental resolution | 5 s | 10 s |
| Full real Miller resolution | 60 s | 120 s |
| Byte-identical artifact retry after source verification | 2 s | 5 s |

## Active blockers

### PERF-001 — Family-store graph reads hydrate the full repository in every MCP host

- **Status:** Accepted on `e46e72e2`. The disk-backed read path, bridge-parity follow-up, bounded telemetry, and
  rebuilt-host wall/PSS gates are complete.
- **Observed:** Warm Miller `context` took 6.9 s. `trace` took 5.9–6.0 s and `impact` took 6.2 s.
- **Memory:** Four Miller hosts retained about 5.4 GB PSS total; individual hosts retained about 1.0–2.0 GB.
- **Root cause:** `WorkspaceIndexProvider.ResolveCurrent` and `ResolveRegistered` call
  `RepositoryIndexLoader.LoadSession` for family-store reads and cache a complete `MillerRepositoryIndex` plus
  `SymbolGraph` per process and generation.
- **Fix:** Carry `ISymbolLookupIndex` and `ISymbolGraphReachability` separately. Use the generation-checked FTS
  sidecar for lookup and a pinned-session `SqliteSymbolGraphIndex` for bounded graph traversal.
- **Gate:** Provider regression proves the full session-index loader is never called for family-store
  context/impact/trace; affected parity tests pass; one bounded dogfood call meets wall and PSS budgets.
- **Focused evidence:** 361/361 affected tests passed in 0.96 s test time (6.21 s command wall). Family bridge
  loading is deferred, runs once per read context only when bridge mode is requested, and matches legacy output.
- **Telemetry:** Family reads now report real provider resolve time, lookup count/time, graph count/time, and bounded
  provider-cache entries. The generation-cached lookup wrapper preserves object identity and each read reports only
  its own counter deltas. Exact telemetry/cache tests passed 4/4 and the affected ceiling passed 456/456.
- **Acceptance:** Final warm context/impact/trace completed in 1,938.450/1,260.196/145.721 ms. Context peaked at
  151,516 KB PSS and 194,784 KB RSS; the same host's 3 s idle sample peaked at 161,214 KB PSS / 204,824 KB RSS.

### PERF-002 — Family-store freshness rebuilds the full repository in every host on every revision

- **Status:** Accepted on `e46e72e2`; focused no-load coverage and rebuilt-host idle memory evidence are complete.
- **Observed:** A reader with no active tool call reached 101.5 GB logical reads, 24.8 million read syscalls,
  about 1.0 GB RSS, and sustained 40–60% CPU. A newly restarted host reached 114% CPU and about 948 MB RSS.
- **Log evidence:** `FreshnessService` reported `rebuilt + swapped index` for revisions 8867 and 8868 in several
  processes. Startup loaded 223,716 symbols and took 6.8 s before the first tool call.
- **Root cause:** Store bootstrap calls `RepositoryIndexLoader.LoadSession`; every `FreshnessService` then calls it
  again whenever the store log advances. The legacy `IndexHolder` assumes every host needs a complete immutable
  in-memory index even though family-store read tools can query pinned disk views.
- **Fix direction:** Make the holder lazy for family-store mode, keep revision/artifact/count metadata eager, and
  replace the lazy generation on refresh without evaluating it. Only legacy mode and an explicit edit that truly
  needs the legacy repository object may materialize it.
- **Implemented:** `IndexHolder` atomically publishes eager revision/artifact/count metadata with a single-flight
  lazy repository. Bootstrap and freshness pin that factory to the captured family/view/generation identity;
  generation drift fails explicitly. Every current family-store read route and workspace status uses metadata or
  disk-backed projections, while legacy routes retain one atomic repository/revision snapshot.
- **Focused evidence:** 246/246 affected tests passed in 1.0 s test time (7.1 s command wall), including the real
  FreshnessService poll seam, provider/status no-load paths, legacy atomicity, and generation-race rejection.
- **Gate:** Store bootstrap and a revision advance invoke no repository loader; holder metadata advances; a
  deliberately invoked legacy/edit path still loads once; idle post-refresh host stays below the PSS budget.
- **Acceptance:** Focused coverage proves bootstrap and revision advance do not evaluate the repository loader.
  Final candidate idle retained 161,214 KB PSS / 204,824 KB RSS, below the 350 MB budget.

### PERF-010 — Registering a Miller worktree can block on its own index lifecycle

- **Status:** Newly observed; root cause not yet isolated.
- **Observed:** `workspace open` for the active Miller performance worktree did not return within about 44 seconds
  and was terminated once. The canceled tool call left its host PID 1906242 processing extractor PID 1906660;
  after more than 90 seconds the extractor was still about 115% CPU with 1.8 GB RSS and the host about 1 GB RSS.
  Both exact orphaned processes were then terminated and the open was not retried.
- **Risk:** An agent cannot afford a tens-of-seconds registration tax before code exploration, especially when
  several worktrees are active on a constrained Windows laptop.
- **Next diagnosis:** After PERF-002 removes per-host eager hydration, run one bounded registration with phase
  telemetry and verify request cancellation stops the supervised extractor while the MCP host remains responsive.
  If it remains slow, isolate
  registry/refresh/extractor/sidecar phases before changing behavior.
- **Gate:** Warm already-indexed worktree registration/open meets the 5 s cold-read development budget and does
  not launch an unnecessary full scan or retain a second workspace-sized repository graph; caller cancellation
  stops the request's extractor and the existing MCP host returns to bounded idle memory.

### PERF-003 — Family-store inspect reloads a workspace-sized symbol projection

- **Status:** Accepted on `e46e72e2`; the final-HEAD warm inspect completed in 254.855 ms.
- **Observed:** Exact `inspect` calls took roughly 1.7–2.0 s each after a new generation; the current soft budget is
  500 ms.
- **Root cause:** `ResolveCurrentSymbolRead` uses `SymbolSearchProjectionLoader.LoadSession` instead of the existing
  generation-checked on-disk `FtsSymbolSearchIndex`. The projection is cached only until the next generation.
- **Fix:** Route symbol-read lookup through the same FTS sidecar path as symbol search. Preserve the bounded
  projection only for the explicit sidecar-off escape hatch.
- **Telemetry:** Commit `75e86c0a` measures the real disk-backed lookup calls and elapsed time on each family read,
  so the rebuilt-host dogfood gate can distinguish provider setup from lookup work without materializing the holder.
- **Gate:** Default-on sidecar test proves the session projection loader is not called and exact inspect parity is
  unchanged; bounded dogfood meets the 500 ms warm budget.
- **Acceptance:** The exact `WorkspaceIndexProvider` overview returned one correct result in 254.855 ms with
  61,120 KB peak PSS / 102,580 KB RSS and no graph work.

### PERF-004 — Julie resolved-pending rechecks issue one identifier locator query per row

- **Status:** Accepted through the measured finalization fix on Julie `ab3aa957`; the locator batching attempt was
  rejected because it removed statements without improving wall time.
- **Observed:** A scoped resolve processed 199,123 rows for 47 names in 520,055 ms. Its process recorded about
  98.1 GB logical reads and 24.1 million read syscalls with only about 26 MB RSS.
- **Disproven hypothesis:** A repeated-name/high-fanout fixture did not amplify top-level candidate reads even when
  source confidence varied to defeat full outcome caching. It executed PrimeWindow once for 300 rows and
  TopLevelNamed no more than three times. Do not add a top-level page cache based on the production symptom alone.
- **Also disproven:** The same fixture executed FilteredNameSummary once for two rows; the existing summary cache
  already collapses that shape. Do not add a filtered-summary cache based on the production symptom.
- **Telemetry:** Fourteen enum-backed query families now record executions and rows with no dynamic labels. The
  exact fixture also recorded IdentifierHydration once for 32 rows while preserving 32/32 ambiguous outcomes.
- **Bounded attempt:** The one Miller-scale fixture-preparation pass hit its 60 s bound and was not rerun. Its
  preserved scratch had a generation-1 diagnostic view, zero ready bases, a 65.8 MB partial WAL, and a 91.5 MB
  work DB, so starting the scoped worker would not have been faithful.
- **Timeout evidence fix:** Test-only diagnostics now serialize all fourteen families and atomically persist live
  snapshots at exponentially growing execution thresholds, so a future resolver timeout retains counters without
  adding a public CLI/report contract or per-query filesystem work.
- **Faithful replay:** A reflink clone of the prior clean 1,538-file fixture reused its ready 392,526-identifier
  predecessor base. The one scoped diagnostic completed in 49.81 s process wall; `run_resolution_session` was
  24.848 s and finalization/other process work was about 24.96 s.
- **Query evidence:** LocateIdentifier executed 10,804 times (72.12% of 14,980 query executions), exactly the
  pending count. IdentifierHydration read 381,722 rows, PrimeWindow 313,107, and PendingHydration 89,930 rows.
  Both locator indexes exist; the span query still reports a temporary ORDER BY B-tree, but an index-only tweak
  cannot remove 10,804 calls.
- **Disproven caller attribution:** A batched `materialized_relationship_covers` implementation reduced an exact
  synthetic fixture from 35 locator calls to zero, but the one faithful replay remained 49.63 s wall / 24.740 s
  resolver with LocateIdentifier still 10,804 and RelationshipCoverage zero. That uncommitted slice is being
  removed; it does not fix the production bottleneck.
- **Second disproven caller attribution:** Batching exact co-location hydration in `load_resolved_pending_page`
  passed a real scoped 8-row RED/GREEN (8 locator calls to zero; three hydration statements at window three), but
  the faithful replay again remained 49.77 s wall / 24.738 s resolver with LocateIdentifier still 10,804 and the
  new family zero. That uncommitted slice was removed too; resolved-pending recheck is not the production caller.
- **Exact caller evidence:** Test-only caller telemetry committed at Julie `5089c3a2` records fixed per-phase
  locator counts. Its one faithful replay reported Pending=10,804 and ResolvedPending=Relationships=Other/Unset=0.
  The earliest retained snapshot already had Pending=8,109 at 1.047 s with every other bucket zero.
- **No-win implementation rejected:** The exact measured Pending slice reduced LocateIdentifier from 10,804 to
  zero and total candidate statements from 14,980 to 4,176, with digest/rows unchanged. Its single replay was
  nevertheless 50.46 s wall / 25.418 s resolver versus 49.88 s / 24.813 s baseline—0.58–0.61 s slower. The
  enriched page join used both locator indexes but added temporary GROUP BY/ORDER BY B-trees. The uncommitted
  production slice is being removed; statement count was not wall-clock load-bearing on this host.
- **Remaining measured work:** PrimeWindow still reads 313,107 rows, IdentifierHydration 381,722, and
  PendingHydration 89,930. Separately, exact finalization consumes about 23.7–25.0 s—roughly half total wall.
- **Next diagnosis:** Instrument `finish_exact` at fixed boundaries (prior-overlay materialization, totality check,
  base row streaming, target validation/integrity, sync/publication) and optimize its largest measured phase.
- **Finalization phase evidence:** One fixed-boundary replay completed in 49.98 s wall with 24.897 s in the resolver
  and 24.947 s in `finish_exact`. Exclusive finalization time was: prior overlay approximately zero;
  identifier totality 5.802 s; writer initialization 1 ms; source versions 23 ms; identifier-row streaming
  **14.304 s (57.3%)**; pending rows 268 ms; writer finish/integrity/publication 4.537 s; scratch cleanup 12 ms.
  Digest `b8833220...`, 392,526 identifiers, 10,804 pending rows, and integrity remained exact. The next RED targets
  only identifier-row streaming; artifact-writer deep telemetry is not justified yet.
- **Measured identifier writer fix:** `ResolutionBaseWriter` now keeps one cached identifier INSERT statement behind
  its unchanged streaming API. The exact 100,000-row RED took 3.415 s against a 2.5 s ceiling; GREEN took 1.715 s.
  One post-fix faithful replay reduced identifier-row streaming from 14.304 s to 7.547 s (-47.2%), `finish_exact`
  from 24.947 s to 18.161 s, and end-to-end wall from 49.98 s to 43.10 s (-13.8%). Resolver time stayed comparable
  at 24.710 s. Digest, 392,526 identifiers, 10,804 pending rows, publication identity, crash boundaries, and
  integrity remained exact. Remaining finalization costs are totality 5.791 s and writer finish 4.524 s.
- **Gate:** Closed. The exact writer RED/GREEN and faithful replay reduced end-to-end resolution to 43.10 s while
  preserving digest, rows, crash safety, publication identity, and integrity.

### PERF-011 — Family-store context graph reach still exceeds the interactive budget

- **Status:** Accepted on `e46e72e2`; context, impact, trace, inspect, and memory gates pass on rebuilt candidates.
- **Exact request:** `context` query `how family store read context resolves symbols and graph`, entry symbol
  `WorkspaceIndexProvider`, token budget 1,200, max hops 1, default semantics, persistent initialized candidate.
- **Acceptance miss:** Candidate PID `1998081`, cid `019ff4c2-4421-7657-b826-5cfc556505c7`, produced no response in
  7,002.755 ms. It stopped after pivot ranking and before the fixed `graph_reach` completion event. Impact and trace
  were not run because context failed the 2 s gate.
- **Resource evidence:** 764 CPU ticks, 7,806,745,038 logical-read characters, 1,909,115 read syscalls,
  5,096,174,958 logical-write characters, 1,244,311 write syscalls, 114,556,928 physical-write bytes, 156,774 KB
  peak PSS, 149,436 KB peak private memory, and 199,108 KB peak RSS.
- **Phase evidence:** resolve 21 ms; semantic seeds 592 ms; source rescue 579 ms; full-query retrieval 322 ms;
  per-term retrieval 443 ms; anchor resolution 672 ms; pivot ranking 7 ms. No completed context telemetry row exists
  after cancellation, so `read_graph_count`/`read_graph_ms` were not persisted.
- **Instrumentation verification:** The fixed phase observer reports only completed real boundaries, preserves the
  early-return shape, and reports the complete non-empty bundle shape. The two focused order tests pass together;
  phase labels are fixed and every persisted log event carries the request correlation ID. The Release build passes
  with zero warnings and zero errors.
- **Final clean-HEAD acceptance:** Release was rebuilt from `85c51f81` and the isolated server identified itself as
  `1.18.1+85c51f813492`. The one permitted context call (PID `2019481`, cid
  `019ff4f3-051a-70b5-afc1-6b015b52a3a1`) still timed out at 7,007.282 ms after `pivot_ranking`; no `graph_reach`
  event or completed telemetry row was produced. It consumed 769 CPU ticks, 7,420,905,628 logical-read characters,
  4,952,562,410 logical-write characters, 112,713,728 physical-write bytes, and peaked at 159,880 KB PSS / 202,076
  KB RSS. Impact, trace, and steady-idle were skipped because context missed first.
- **Split-query-family acceptance:** Release was rebuilt from graph commit `b71263c1`; the server identified itself
  as `1.18.1+b71263c1c0f6`. The one permitted context call (PID `2028695`, cid
  `019ff506-221a-7c19-9182-91eaafc79cc1`) still timed out at 7,007.190 ms after `pivot_ranking`, with no
  `graph_reach` event or completed telemetry row. It consumed 758 CPU ticks, 7,165,003,276 logical-read characters,
  4,894,812,906 logical-write characters, 120,950,784 physical-write bytes, and peaked at 157,155 KB PSS / 199,148
  KB RSS. Impact, trace, and steady-idle were skipped because context missed first.
- **Statement-phase diagnosis:** Release was rebuilt from `921ccdff`; the one default-on diagnostic (PID `2034501`,
  cid `019ff50f-2b63-7023-8c54-52b83b6305fe`) timed out at 7,006.926 ms. Exact PID/cid rows completed
  `relationship_forward` in 519 ms / 1 row, `relationship_reverse` in 512 ms / 1 row,
  `unresolved_name_forward` in 1,432 ms / 0 rows, and `unresolved_name_reverse` in 1,973 ms / 65 rows; the last
  phase completed during shutdown after cancellation was requested. No `family_resolution`, `supplemental`,
  `completion`, or outer `graph_reach` event completed. The diagnostic consumed 6,405,642,896 logical-read
  characters and 130,494,464 physical-write bytes, peaking at 161,060 KB PSS / 203,408 KB RSS.
- **Family-arm diagnosis:** Release was rebuilt from `a9bf810b`; the one default-on diagnostic (PID `2037968`, cid
  `019ff514-2b35-7882-836b-3a82179bc5f8`) timed out at 7,006.932 ms. Exact graph order was
  `relationship_forward` 501 ms / 1 row, `relationship_reverse` 498 ms / 1 row, `unresolved_name_forward`
  1,396 ms / 0 rows, and `unresolved_name_reverse` 1,929 ms / 65 rows. None of the eight new identifier/pending
  base/delta forward/reverse family-resolution arms completed, so the deepest boundary remains
  `unresolved_name_reverse`; the next stall is before the first family arm completion. No `family_resolution`,
  `supplemental`, `completion`, or outer `graph_reach` completed. The run consumed 6,954,489,644 logical-read
  characters and 139,198,464 physical-write bytes, peaking at 158,112 KB PSS / 200,136 KB RSS.
- **Candidate-shape diagnosis:** Release was rebuilt from `83108ad3`; the one default-on diagnostic (PID `2042776`,
  cid `019ff51c-73a2-7e98-8001-65705c2b120f`) timed out at 7,007.205 ms. The first completed graph event,
  `relationship_forward` at 505 ms / 1 row, reported candidate count 4 and the exact capped sample
  `[a6a374fb8554e68e3a7a0b217670d32a, ac38a31eba3de6a7a7fcb778bf24e33a,
  9639df0e830f9b3520b25bb6b3aa837a, 72d24b5950320bbbd03e1bf7dca3e52a]`. Later phases retained the same shape:
  `relationship_reverse` 499 ms / 1 row, `unresolved_name_forward` 1,400 ms / 0 rows, and
  `unresolved_name_reverse` 1,927 ms / 65 rows. No family arm or outer `graph_reach` completed. The run consumed
  7,037,425,780 logical-read characters and 140,709,888 physical-write bytes, peaking at 155,491 KB PSS / 197,708
  KB RSS.
- **Family-name-union bypass acceptance:** Release was rebuilt from `7d712f8b`; the one default-on acceptance (PID
  `2054929`, cid `019ff533-f2b5-747e-b395-38f4c853b9ef`) still timed out at 7,001.628 ms. Outer phases completed
  through pivot ranking: resolve 20 ms, semantic seeds 616 ms, source rescue 580 ms, query retrieval 424 ms, term
  retrieval 533 ms, anchor resolution 680 ms, and pivot ranking 6 ms. Graph phases completed
  `relationship_forward` 508 ms / 1 row, `relationship_reverse` 505 ms / 1 row, `unresolved_name_forward`
  1,393 ms / 0 rows, and `unresolved_name_reverse` 1,917 ms / 65 rows, with the same four pivots. No family arm,
  outer `graph_reach`, output, or telemetry row completed. The run consumed 6,814,735,084 logical-read characters
  and 137,048,064 physical-write bytes, peaking at 154,042 KB PSS / 199,704 KB RSS. Impact and trace were skipped.
- **Forwarded-capability acceptance:** Release was rebuilt from `d42f2626`; context finally completed successfully
  instead of timing out, but at 4,332.033 ms it still missed the 2 s hard gate. Graph reach completed in 1,296 ms:
  unresolved-name arms fell from seconds to 0/1 ms, all eight family arms completed in 0–20 ms each, family
  resolution took 43 ms / 180 rows, supplemental 168 ms / 24 rows, and graph completion 1,257 ms / 155 rows.
  Before graph reach, semantic/source/query/term/anchor work still totaled 2,839 ms. Completed telemetry reported
  1,602 lookups / 1,929 ms and one graph call / 1,296 ms. The successful 3,378-byte, 10-result response consumed
  3,863,767,755 logical-read characters and 23,568,384 physical-write bytes, peaking at 151,849 KB PSS / 198,456
  KB RSS. Impact, trace, and idle were skipped because context remained over gate.
- **Relationship-view bypass acceptance:** Release was rebuilt from `8e711985`; context completed successfully in
  3,153.484 ms, improving another 1.18 s but still missing the 2 s hard gate. Graph reach fell from 1,296 ms to
  290 ms: relationship arms fell to 1/0 ms, unresolved-name arms remained 0/1 ms, family arms stayed 0–20 ms, and
  graph completion fell to 250 ms / 155 rows. The remaining dominant evidence is before graph reach: semantic,
  source, query, term, and anchor phases consumed 2,672 ms, while completed telemetry reported 1,602 lookups /
  1,750 ms and one graph call / 289 ms. The successful response consumed 2,455,032,764 logical-read characters,
  only 380,928 physical-write bytes, and peaked at 153,819 KB PSS / 200,876 KB RSS. Impact, trace, and idle were
  skipped because context remained over gate.
- **Lookup-family diagnosis:** Release was rebuilt from `264d2e8a`; the one diagnosis completed successfully in
  3,423.838 ms. Fixed lookup deltas prove search owns the largest remaining measured lookup cost: query retrieval
  used Search 4 / 811 ms, ResolveDoc 68 / 13 ms, FindBySymbolId 61 / 9 ms; term retrieval used Search 9 / 331 ms,
  ResolveDoc 482 / 64 ms, FindBySymbolId 235 / 31 ms; anchor resolution used Search 8 / 310 ms, ResolveDoc 272 /
  36 ms, FindByName 1 / 22 ms, FindBySymbolId 300 / 39 ms, ResolveIndexedFilePath 1 / 14 ms. Graph reach and
  candidate ordering had no lookup delta; hydration used FindBySymbolId 157 / 19 ms; file neighbours used
  FindByFilePath 4 / 3 ms. Total Search was 21 calls / 1,452 ms of the telemetry total 1,602 calls / 1,708 ms.
  Graph reach remained 286 ms. The run consumed 2,455,051,247 logical-read characters and 425,984 physical-write
  bytes, peaking at 156,301 KB PSS / 203,452 KB RSS.
- **Search-shape diagnosis:** Release was rebuilt from `4de81d3f`; the one diagnosis completed successfully in
  3,569.835 ms. Query retrieval issued first 1 / 254 ms / 0 rows, mode variant 1 / 270 ms / 18 rows, and window
  variant 2 / 357 ms / 50 rows; its orthogonal boolean split was AND 2 / 425 ms / 0 rows versus OR 2 / 456 ms /
  68 rows. Term retrieval issued first 8 / 341 ms / 272 rows and window variant 1 / 6 ms / 210 rows, all OR 9 /
  347 ms / 482 rows. Anchor resolution issued exact repeat 8 / 325 ms / 272 rows, all OR 8 / 325 ms / 272 rows.
  The other four phases issued no searches; no calls were dropped. Final totals were first 9 / 595 ms / 272 rows,
  mode 1 / 270 ms / 18 rows, window 3 / 363 ms / 260 rows, exact repeat 8 / 325 ms / 272 rows, with AND 2 /
  425 ms / 0 rows and OR 19 / 1,128 ms / 822 rows. Lookup telemetry was 1,602 calls / 1,818 ms, including Search
  21 / 1,554 ms; graph reach was 291 ms. The run consumed 2,455,137,618 logical-read characters and 401,408
  physical-write bytes, peaking at 156,415 KB PSS / 199,792 KB RSS.
- **Search-reuse acceptance:** Release was rebuilt from `23f08106`; context completed successfully in 3,060.308 ms,
  an improvement but still over the 2 s gate. Actual Search work fell from 21 calls to 13: first query 9 / 674 ms /
  272 rows, mode variant 1 / 242 ms / 18 rows, window variant 3 / 373 ms / 260 rows, exact repeat 0, and cache hit
  8 / 0 ms / 272 rows. On the boolean axis, actual work was AND 2 / 416 ms / 0 rows and OR 11 / 874 ms / 550
  rows; dropped 0. All eight anchor repeats were cache hits, reducing anchor resolution to 344 ms, but query/term
  actual searches still cost 393/554 ms and returned broad OR windows. Lookup telemetry fell to 1,594 calls /
  1,567 ms, including Search 13 / 1,291 ms; graph reach remained 285 ms. The 3,378-byte, 10-result response consumed
  2,066,491,374 logical-read characters and 438,272 physical-write bytes, peaking at 155,465 KB PSS / 198,784 KB
  RSS. Impact, trace, and idle were skipped because context remained over gate.
- **Empty-AND short-circuit acceptance:** Release was rebuilt from clean `971d9deb`; context completed successfully
  in 2,531.825 ms, another improvement but still over the 2 s gate. Query retrieval issued first 1 / 3 ms / 0
  rows, mode variant 1 / 247 ms / 18 rows, and window variant 2 / 190 ms / 50 rows; its boolean split was AND 2 /
  4 ms / 0 rows and OR 2 / 437 ms / 68 rows. Term retrieval issued first 8 / 363 ms / 272 rows and window variant
  1 / 5 ms / 210 rows, all OR 9 / 368 ms / 482 rows. Anchor resolution reused cache 8 / 0 ms / 272 rows and
  issued no Search calls. Final actual totals were first 9 / 366 ms / 272 rows, mode 1 / 247 ms / 18 rows,
  window 3 / 195 ms / 260 rows, exact repeat 0, cache hit 8 / 0 ms / 272 rows, AND 2 / 4 ms / 0 rows, and OR
  11 / 805 ms / 550 rows; dropped 0. Lookup telemetry was 1,594 calls / 1,085 ms, including Search 13 / 809 ms;
  graph reach was 305 ms. The 3,378-byte, 10-result response consumed 1,861,922,088 logical-read characters and
  393,216 physical-write bytes, peaking at 140,406 KB PSS / 183,856 KB RSS. Impact, trace, and idle were skipped
  because context remained over gate.
- **FTS-stage diagnosis:** Release was rebuilt from clean `6913b8cc`; the one diagnosis completed successfully in
  2,680.376 ms. Query retrieval opened four FTS connections and spent 225 ms returning 33,930 word candidates,
  209 ms scoring them, and 7 ms on two empty trigram windows; the two empty AND probes themselves cost 1 ms.
  Term retrieval opened nine connections and spent 201 ms returning 23,138 word candidates, 73 ms scoring them,
  and 152 ms returning 1,800 trigram candidates. Final FTS totals were connection 13 / 0 ms, AND probe 2 / 1 ms /
  0 rows, word candidates 11 / 426 ms / 57,068 rows, word scoring 11 / 283 ms / 57,068 rows, trigram candidates
  11 / 159 ms / 1,800 rows, trigram scoring 11 / 0 ms / 810 rows, and ordering 11 / 0 ms / 550 rows. Search
  remained 13 / 877 ms, lookup 1,594 / 1,148 ms, graph 299 ms, and all later lookup phases issued zero FTS work.
  This proves broad word-candidate loading/scoring owns 709 ms of the 868 ms measured inside FTS, with the term
  trigram candidate queries another 152 ms. The 3,378-byte response remained correct at 10 results / 845 tokens.
- **Unused-trigram bypass acceptance:** Release was rebuilt from clean `d0cf7c34`; context completed successfully
  in 2,509.602 ms, still over the 2 s gate. FTS trigram work fell from 11 queries / 159 ms / 1,800 candidates to
  one query / 5 ms / 200 candidates, but word work remained dominant: 11 candidate queries / 451 ms / 57,068
  rows plus 11 scoring passes / 266 ms. Search was 13 calls / 729 ms and total lookup was 1,594 / 992 ms; graph
  remained 289 ms. Outer semantic/source/query/term/anchor phases were 377/677/248/374/335 ms. The correct
  3,378-byte, 10-result response consumed 1,627,915,275 logical-read characters and 430,080 physical-write bytes,
  peaking at 140,729 KB PSS / 187,924 KB RSS. Impact, trace, and idle were skipped because context remained over
  gate.
- **Deferred-word-hydration acceptance:** Release was rebuilt from clean `e97bf3de`; context completed successfully
  in 2,104.074 ms, only 104 ms over the 2 s gate. Word-candidate work fell from 451 to 228 ms and word scoring
  from 266 to 147 ms while the new bounded hydration stage loaded only 402 final rows in 5 ms; total Search fell
  from 729 to 394 ms and lookup from 992 to 654 ms. The remaining largest outer phases are source rescue 626 ms,
  anchor resolution 337 ms, graph 282 ms, semantic seeds 275 ms, and term retrieval 253 ms. The correct
  3,378-byte, 10-result response consumed 1,629,859,578 logical-read characters and 434,176 physical-write bytes,
  peaking at 123,574 KB PSS / 170,600 KB RSS. Impact, trace, and idle were skipped because context remained over
  gate.
- **Allocation-free-scoring final acceptance:** Release was rebuilt from clean `02deba5e`; the one final context
  completed correctly but regressed to 2,515.301 ms, missing the 2 s hard gate. FTS word candidate/scoring was
  262/227 ms for the same 57,068 rows, bounded hydration remained 5 ms / 402 rows, total Search was 508 ms,
  lookup 778 ms, and graph 302 ms. The largest outer phases were source rescue 857 ms, semantic seeds 386 ms,
  anchor resolution 359 ms, and graph 302 ms. The 3,378-byte, 10-result response consumed 1,629,847,106
  logical-read characters and 430,080 physical-write bytes, peaking at 121,234 KB PSS / 168,440 KB RSS. Impact,
  trace, and idle were skipped because context remained over gate; the unchanged request was not repeated.
- **Source-rescue diagnosis:** Release was rebuilt from clean `3e356d80`; the one diagnosis completed in
  2,485.694 ms. The new content-FTS stages prove scoring dominates source rescue: five scoring passes consumed
  561 ms to select five results, versus one widened-candidate query at 16 ms / 3,786 rows and five hydration
  queries at 44 ms / 1,832 rows; connection, nine document-frequency queries, the empty strict query, and final
  ordering totaled 1 ms. Content scoring therefore owns 561 ms of the 882 ms outer source-rescue phase. Symbol
  FTS used 237 ms for 57,068 candidates, 242 ms scoring, and 4 ms bounded hydration; lookup was 1,594 / 760 ms
  and graph 288 ms. The correct response consumed 1,629,863,538 logical-read characters, peaked at 122,059 KB
  PSS / 169,104 KB RSS, and preserved 3,378 bytes / 10 results / 845 tokens.
- **Source-scoring subphase diagnosis:** Release was rebuilt from clean `1f7fa498`; the one diagnosis completed in
  2,517.657 ms. Token scoring and phrase detection consumed 518 ms across five batches / 1,832 hydrated rows;
  snippet selection added 60 ms but ran for only 116 survivors. Candidate filtering was 1 ms, symbol mapping and
  result construction rounded to 0 ms, and aggregate scoring was 580 ms. This proves nearly all source-rescue
  scoring cost is repeatedly tokenizing and scoring the 1,832 fully hydrated chunks, not snippet extraction or
  symbol lookup. The outer source-rescue phase was 888 ms; output remained correct at 3,378 bytes / 10 results /
  845 tokens, with 121,565 KB peak PSS / 168,852 KB RSS.
- **Deferred-source-hydration acceptance:** Release was rebuilt from clean `fa27442e`; context improved to
  2,385.347 ms but still missed the 2 s gate. Narrow scoring processed 1,832 candidate rows in 201 ms, then only
  116 survivors were hydrated in 7 ms, phrase-verified in 83 ms, and snippet-scored in 82 ms. Source rescue fell
  from 888 to 704 ms, while symbol Search was 542 ms, lookup 814 ms, and graph 308 ms. The correct 3,378-byte,
  10-result response consumed 1,605,764,774 logical-read characters and peaked at 146,853 KB PSS / 194,088 KB
  RSS. Impact, trace, and idle were skipped because context remained over gate.
- **Fused-source-analysis acceptance:** Release was rebuilt from clean `7556ef25`; context improved to
  2,288.059 ms but still missed the 2 s gate. Fused raw-text analysis consumed 76 ms / 116 rows versus the prior
  separate 83 ms phrase verification plus 82 ms snippet selection; source rescue fell from 704 to 609 ms. Symbol
  Search remained 547 ms, total lookup 825 ms, and graph 294 ms. The correct 3,378-byte, 10-result response used
  238 CPU ticks and 1,605,754,253 logical-read characters, peaking at 148,243 KB PSS / 195,332 KB RSS. Impact,
  trace, and idle were skipped because context remained over gate.
- **Content-provider diagnosis:** Release was rebuilt from clean `a0613d60`; the one diagnosis completed correctly
  in 2,253 ms server wall / 2,254 ms persisted telemetry, still missing the 2 s gate. Provider work occurred only
  in SourceRescue: one resolve took 238 ms, comprising one 6 ms read-session open, one cache miss, and one 230 ms
  index load; every later provider delta was zero. Source rescue was 580 ms, including content FTS widened
  candidates 47 ms / 3,786 rows, narrow scoring 196 ms / 1,832 rows, hydration 8 ms / 116 rows, and fused raw-text
  analysis 74 ms / 116 rows. Symbol Search was 555 ms, total lookup 1,594 calls / 830 ms, and graph 295 ms. The
  response remained correct at 3,378 bytes / 10 results / 845 tokens. Exact PID/cid log evidence was retained,
  but the harness result exceeded the orchestration output budget after process exit, so its sampled CPU/I/O/PSS
  object was not recoverable; the candidate was not repeated and PID `2150085` was confirmed absent.
- **Deferred-content-index acceptance:** Release was rebuilt from clean `fc634b50`; context improved to
  2,107.919 ms but still missed the 2 s gate by 107.919 ms. The intended provider cost collapsed: Resolve fell
  from 238 to 10 ms and IndexLoad from 230 to 2 ms, while ReadSessionOpen remained 6 ms and the single cache miss
  was preserved. Source rescue fell from 580 to 405 ms, but still spent 194 ms narrow-scoring 1,832 rows, 65 ms
  widening to 3,786 candidates, 70 ms analyzing 116 hydrated rows, and 35 ms hydrating 8,402 symbol spans. Symbol
  Search was 550 ms, total lookup 1,594 calls / 838 ms, and graph 292 ms. The correct 3,378-byte, 10-result
  response consumed 220 CPU ticks and 1,614,721,855 logical-read characters, peaking at 147,114 KB PSS / 193,184
  KB RSS. Impact, trace, and idle were skipped because context remained over gate.
- **Single-pass FTS final acceptance:** Release was rebuilt from clean `e46e72e2`; context passed the 2 s hard
  gate at 1,938.450 ms, then the same initialized host passed impact at 1,260.196 ms and trace at 145.721 ms.
  Single-pass word scoring reduced symbol FTS scoring from 242 to 116 ms and total Search from 550 to 375 ms;
  context lookup fell from 838 to 658 ms. Context output remained exact at 3,378 bytes / 10 results / 845 tokens.
  Context consumed 194 CPU ticks and 1,611,105,366 logical-read characters with 151,516 KB peak PSS / 194,784 KB
  RSS. The post-read 3 s idle sample added 68 CPU ticks, 777,199 logical-read characters, zero physical reads,
  and peaked at 161,214 KB PSS / 204,824 KB RSS. All required read lanes passed without a repeat.
- **Final-HEAD warm inspect acceptance:** The already-built `e46e72e2` Release candidate returned the exact
  `WorkspaceIndexProvider` overview successfully in 254.855 ms, passing the 500 ms gate. Persisted telemetry was
  235 ms with one result / 3,165 bytes / 792 estimated tokens, 28 lookups / 69 ms, zero graph work, and 21 ms
  provider resolution. The isolated process used 26 CPU ticks, 338,295,847 logical-read characters, zero physical
  reads, and peaked at 61,120 KB PSS / 102,580 KB RSS. The one-run lane exited cleanly without touching registry
  state or any existing host.
- **Gate:** Closed. The final one-run sequence passed context at 1,938.450 ms, impact at 1,260.196 ms, trace at
  145.721 ms, warm inspect at 254.855 ms, and the 350/600 MB retained/peak PSS budgets.

### PERF-009 — Bridge trace still needs a bounded family-store representation

- **Status:** Bounded lazy bridge representation complete on `1fa03ac9`; real bridge budget gate pending.
- **Finding:** The first lean-context implementation returned `bridge_requires_full_index` for family-store bridge
  mode. That avoids hydration but changes an existing public result and violates output parity.
- **Immediate fix:** Preserve parity with a lazy bridge-only loader from the pinned session; ordinary lookup/refs/path
  calls must not evaluate it.
- **Remaining performance risk:** The existing bridge builder consumes whole-corpus symbol details. If a real bridge
  trace exceeds the 2 s/5 s budgets or retains more than 350 MB, move the bridge graph into a generation-keyed
  Miller sidecar or redesign the builder around persisted bridge nodes/edges.
- **Gate:** Ordinary reads invoke the bridge loader zero times; one bridge call invokes it once and matches legacy
  output; bounded dogfood records wall/PSS and either closes the item or proves the sidecar redesign is required.

### PERF-005 — Scope selection still permits large full-like resolution work

- **Status:** Accepted with the scope crossover fix plus Julie writer optimization `ab3aa957`.
- **Before:** One changed file expanded to 516,065 scoped rows versus 533,152 visible full rows and spent about
  20 minutes in resolution. Another incident selected 531,492 rows and spent about 20.7 minutes.
- **After:** The real host selected full with `resolution_scope_crossover` and completed scope + resolution + diff
  in about 165.5 s, roughly 7.5× faster but still above the 60 s development budget.
- **Gate:** Closed by the faithful 43.10 s replay with unchanged digest, rows, publication identity, and integrity.

## Fixed, awaiting integrated release gate

### PERF-006 — Byte-identical cross-key artifact imports rematerialize every file

- **Status:** Fixed on Julie `main` commit `70cd205f`.
- **Root cause:** Same-key replay exited in the adapter, but a new idempotency key always ran every materialization
  chunk before discovering the exact manifest already existed.
- **Fix:** Fresh preflight/hash plus terminal byte-identical origin permits a private generation/hash hint; the
  executor re-verifies source metadata and transactionally rechecks the exact current generation before returning
  `store_from_artifact_reused`.
- **Evidence:** Focused retry changed from one materialization chunk to zero; changed content and incomplete prior
  origin still materialize; focused from-artifact group passed 14/14 in 6.56 s.
- **Remaining gate:** One bounded real large-artifact retry after producer resolution and Miller host amplification
  are fixed. Do not run it earlier.

### PERF-007 — Long artifact import loses its writer lease

- **Status:** Fixed on Julie commit `0500ab1e` and included in current Julie `main` ancestry.
- **Observed:** A 1.029 GB import ran 17.62 s, peaked near 1.024 GB RSS, then failed writer lease fencing and left a
  dead claimed request.
- **Root cause:** The coordinator renewed only before and after an indivisible import quantum, allowing the 15 s
  lease to expire.
- **Fix:** One RAII heartbeat worker per acquired drain renews at one third of the unchanged TTL; only FromArtifact
  permits the renewable long quantum.
- **Evidence:** Coordinator 60/60, import 31/31, resolution adapters 21/21, real current-v3 import, crash/retry
  exactly-once, strict Clippy, and formatting passed.

### PERF-008 — Miller scan/extraction parallelism can saturate a workstation

- **Status:** Existing guard retained; integrated dogfood required.
- **Protection:** Miller always passes `--jobs`; default is `min(4, max(1, ProcessorCount / 2))`. Exit 137 retries at
  one job. This bounds extraction only, not resolver SQLite work or per-host read hydration.
- **Gate:** During final dogfood, record extractor jobs, Miller host CPU, resolver CPU, and peak RSS. No component
  may silently opt back into all-core operation.

## Correctness incidents that caused wasted performance runs

These are closed but remain here because reintroducing them makes performance evidence invalid.

- Missing family-store root was treated as unreadable and blocked RootRebind recovery: fixed in Miller `e4aad35d`
  and narrowed for inaccessible roots in `bc9ceb6a`.
- Extractor downgrade override was caught before leadership policy: fixed in Miller `99a69442`.
- Populated store with a missing member view selected incremental reconcile instead of RootRebind under the explicit
  override: fixed in Miller `47421be3`.
- Scoped closure crossover did not count selected-version predecessor work: fixed in Julie `f39d7263`.

## Verification discipline

- Run one exact RED and one exact GREEN before any affected suite.
- Run each affected suite once per changed source tree; never rerun a green suite on unchanged code.
- Do not overlap Rust and .NET compiles on the performance machine.
- Before a real replay, record PID, command, source commit, store/view/request IDs, starting PSS/RSS, and I/O counters.
- Apply a 60 s hard timeout to the first real development replay. If it expires, retain artifacts/counters and move
  to the newly identified bottleneck instead of repeating it.
- Final release gate requires Linux dogfood plus the existing Windows release build/capacity-store probe; record
  constrained Windows-oriented latency and memory separately from archive inspection.

## Release readiness

Miller and Julie are **not ready to release** while any P0/PERF-001 through PERF-005 acceptance gate is open.
Preparing local release metadata is allowed only after the fixes are integrated and clean; pushing, tagging,
publishing, or advertising a marketplace version still requires explicit user approval.
