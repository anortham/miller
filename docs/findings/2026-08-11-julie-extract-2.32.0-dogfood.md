# julie-extract 2.32.0 Dogfood — Passed

## Result

The released julie-extract 2.32.0 integration is ready for Miller release-candidate gating. Both workspaces are
exact, the current Miller workspace has all derived sidecars ready, public reads and edit preview work, legacy-off
compatibility is green, scoped resolution is faster than forced full, and the published Windows asset plus upstream
Windows CI are verified. No push, tag, publication, or Miller release occurred.

The Julie workspace is honestly degraded only for foreign-workspace semantic vectors: its semantic broker is ready,
but embeddings are produced only by the resident workspace leader. Search and content are current, and no vector
readiness is claimed for that foreign workspace.

## Recovery and correctness

- `/home/murphy/source/julie-extractors` recovered from partial/unpublished state through the public `workspace
  open` path. The recovery reconciled both the new and stale dead requests; both request IDs reached `committed`.
- The recovery completed in 364.057 seconds with maximum RSS 1,633,148 KB. A subsequent refresh completed in
  242.611 seconds.
- The resulting Julie view is readable and exact at manifest generation 2. Search and content are current.
- `/home/murphy/source/miller` is exact with search, content, and vectors current/ready. Its semantic broker is ready.
- Exact state was read from the published family view; Miller did not fabricate exactness from the preserved partial
  legacy artifact and did not serve a stale legacy fallback.

## Defects found and closed during dogfood

- A valid pointer to a missing store root was rejected before `RootRebind`. Miller now distinguishes true absence
  from inaccessible/corrupt roots and keeps the safe refusal behavior for the latter.
- The explicit recovery override was intercepted before leadership policy. It now reaches the existing eligibility
  decision without changing the default refusal path.
- A populated family could contain a serving generation but no published view for the failed workspace. Explicit
  recovery now selects `RootRebind`, re-plans only that unpublished member, and treats only `Planned + ViewNotFound`
  as an absent pre-import state. Other store corruption still propagates.
- julie-extract's long import now heartbeats its writer lease and reconciles stale claimed requests.
- Scoped resolution no longer explodes one changed file into an unbounded closure; producer regression coverage
  protects the corrected expansion.

## Performance evidence

All measurements are report-only wall-clock observations on the same machine; correctness and canonical equality
remain hard gates.

- Clean replay median: scoped default 18.309 seconds versus forced full 31.971 seconds, with zero canonical diffs.
- Real-corpus crossover: 165.512 seconds versus the prior approximately 20-minute pathological run, about 7.4x
  faster.
- Public MCP timings: search 183 ms, inspect 1,819 ms, context 8,935 ms, impact 6,390 ms, trace 6,139 ms, and edit
  preview 26 ms. Edit preview wrote nothing.
- The original 2.31.4 comparison was about 2m24s for full store resolution and roughly 8–12 seconds for context
  after targeted reads. The corrected 2.32 path restores that operating range while retaining scoped/full equality.

## Public and compatibility surfaces

- Candidate version/capabilities, workspace status/health, lexical and semantic search, source/content search,
  patterns, inspect, trace, impact, context, compact/JSON diagnostics, edit dry-run, and cross-workspace reads were
  exercised against exact stores.
- JSON outputs parsed; edit preview completed in 26 ms and wrote nothing.
- `StoreOffBootstrapExportsCurrentStoreBeforeServingLegacy` and
  `ReleasedStoreAndLegacyArtifactProduceEquivalentPublicReads` passed 2/2. The off-switch exports the current view
  before serving legacy mode and does not fall back to an older artifact.
- Task 3's released-producer replay already proved scoped-default and `JULIE_STORE_RESOLUTION_DELTA=off` forced-full
  canonical equivalence; final clean replay again found zero diffs.

## Windows evidence

- Published asset `julie-extract-v2.32.0-x86_64-pc-windows-msvc.zip` matched SHA-256
  `4d42f077e5f118b31178350b5881e5738b34c9d63ce5e520c98b7fd39884be6b`.
- Archive layout, embedded checksum, PE x64 metadata, and embedded `julie-extract 2.32.0` version metadata were
  verified without executing the Windows binary on Linux.
- Upstream commit `076db37d1921013468b9b1882c23707a01341c07` has successful release-build Windows execution and a successful
  `Windows Capacity Store Probe` CI job. This is the Windows runtime evidence; Linux inspection is not presented as
  Windows execution.

## Gate disposition

- PASS: Julie recovery is exact/readable and both previously dead request paths terminalized.
- PASS: Miller is exact with all local sidecars ready; Julie foreign vectors are honestly unavailable.
- PASS: public reads, diagnostics, cross-workspace routing, and non-writing edit preview.
- PASS: legacy-off current-view export with no stale fallback.
- PASS: scoped/default and forced-full canonical equality plus performance evidence.
- PASS: published Windows asset and upstream Windows execution/CI evidence.

Task 4 is complete. Task 5 owns the final Miller branch gate, version/release metadata, and release-candidate decision.
