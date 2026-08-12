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

- **Status:** Code fix complete on `dabcddd7` plus bridge-parity follow-up `1fa03ac9`; bounded read telemetry
  complete on `75e86c0a`; real budget gate pending.
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

### PERF-002 — Family-store freshness rebuilds the full repository in every host on every revision

- **Status:** Code fix complete in `4f7ff626`; real idle PSS/CPU gate pending with rebuilt host.
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

- **Status:** Code fix complete in `dabcddd7`; real 500 ms warm budget gate pending.
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

### PERF-004 — Julie resolved-pending rechecks issue one identifier locator query per row

- **Status:** Root cause isolated after a faithful replay disproved the first caller attribution; corrected batched
  hydration fix in progress.
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
- **Gate:** One fixed finalization phase is proven load-bearing by a bounded replay, then an exact RED/GREEN reduces
  its work without changing digest, rows, crash safety, or publication identity.

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

- **Status:** Scope crossover fix complete on Julie commits `f39d7263` and `fb31da08`; performance budget still open.
- **Before:** One changed file expanded to 516,065 scoped rows versus 533,152 visible full rows and spent about
  20 minutes in resolution. Another incident selected 531,492 rows and spent about 20.7 minutes.
- **After:** The real host selected full with `resolution_scope_crossover` and completed scope + resolution + diff
  in about 165.5 s, roughly 7.5× faster but still above the 60 s development budget.
- **Gate:** PERF-004 must bring the same real corpus below budget without semantic or row differences.

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
