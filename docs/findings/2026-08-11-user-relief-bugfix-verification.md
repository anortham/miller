# User-relief bugfix verification

Date: 2026-08-11

## Candidate inputs

- Miller: `31696926cbc5e8194d753f42122f43d6a5c1e492`
- julie-extractors: `692ef4eb08dafcf55dbc08e7d1e17cc8d3f11c29`
- julie-semantic-sidecar: `35c8f1345694b092eda762035873dd73fb0bfb51`
- Both producer binaries were restored into Miller from those exact source worktrees. The restored binaries
  retain their current package versions, julie-extract `2.31.4` and semantic sidecar `0.1.0`.

## Hard local gates

| Repository | Gate | Result | Report-only wall time |
| --- | --- | --- | ---: |
| Miller | `scripts/test.sh` | 6,356 passed, 4 skipped, 0 failed | 27s wrapper; 38.31s command |
| Miller | `scripts/test.sh scale` | 135 passed, 5 skipped, 0 failed | 264.77s |
| Miller | Release build | 0 warnings, 0 errors | 2.39s |
| semantic sidecar | format / clippy / Rust tests / Python tests | passed; Python 38/38 | 0.18s / 0.21s / 2.57s / 2.88s |
| julie-extractors | format / clippy / default / contract | passed | 2.68s / 0.26s / 27.55s / 236.68s |

`cargo deny check --all-features` could not run because `cargo-deny` is not installed. No tool was installed
during verification; the existing remote dependency-policy gate remains required.

## Relief assertions

### JSON convergence diagnostics

The fast suite exercised the new JSON variants in `ResolutionLayerGuardTests`. Trace, context usage, inspect
overview/full, and impact all render valid standalone diagnostic envelopes whose code is
`resolution_converging`; empty/whitespace attachment coverage also passed. The prior `invalid_json_output`
failure is closed locally.

### Context and reference latency

The source-built Miller CLI queried the registered Miller family store with JSON output:

| Query | Result | Wall time | 2026-08-11 baseline |
| --- | --- | ---: | ---: |
| natural-language context, reference off, 200 tokens, zero hops | valid pivot bundle | 8.12s | about 33s |
| same query, usage references, 800 tokens, zero hops | valid enriched bundle | 8.50s | usage call exceeded about 90s |
| exact `ContextTool Context`, reference off / usage | valid JSON | 7.60s / 7.98s | 6.4s-6.7s exact-symbol class |
| inspect `ContextTool`, overview | valid JSON, exact target id | 6.90s | 6.851s |
| trace `ContextTool`, refs, limit 1 | valid JSON, 1 of 161 references | 6.79s | 10.619s |

The natural-language path is about four times faster than the reported baseline. Exact-symbol calls remain
dominated by process/index startup, consistent with the 9.35s reference-off cold baseline recorded during
implementation. Timings are observations, not test thresholds.

An MCP attempt to register the isolated worktree was terminated after more than 72 seconds. It left no family
store pointer, so source-built CLI measurements used the already registered main Miller family store by its
absolute selector. This does not weaken the exercised read path, but remains independent dogfood evidence that
workspace-open latency still needs observation.

### Semantic activation without restart

An isolated broker started against an empty cache and returned
`ready:false, degraded_reason:"model_not_prepared"`. The prepared, sha-verified model was then made available
to that cache. A health request to the same broker returned `ready:true`, CPU backend, and 384 dimensions in
0.12s; a following `embed_query` returned 384 values in 0.01s. The broker was not restarted.

The Miller fast gate also passed the session parking, converge re-probe, passive existing-broker prepare probe,
semantic-search fallback, vector classification, compact status, and health-action tests. The frozen sidecar
RPC surface is unchanged; those tests supply the Miller-side no-restart proof that the isolated broker replay
does not exercise directly.

### Store bootstrap and capacity probing

The Miller Scale gate used the source-built extractor and passed the real family-store roundtrip and legacy
bootstrap/reuse tests, with improved producer failure detail available on assertion failure. The extractor
default and contract gates passed the native capacity tests and real public store import.

At the time of this source-built run, the Linux host could not prove the original Windows PowerShell failure was
gone. Follow-up released-2.32.0 evidence closes that remote gate: the release-build Windows job and the
`Windows Capacity Store Probe` both succeeded at `076db37d1921013468b9b1882c23707a01341c07`. The published
Windows asset also matched its SHA-256, archive layout, embedded checksum, PE x64 metadata, and 2.32.0 version.
Linux archive inspection is not presented as Windows runtime execution; the CI jobs provide that proof.

### Released 2.32.0 integration follow-up

The real Julie workspace recovered to exact manifest generation 2 after the producer heartbeat/stale-claim fixes;
both request IDs committed. Miller is exact with local search/content/vector sidecars ready. Clean resolution replay
measured 18.309s scoped versus 31.971s forced full with zero canonical diffs, while the previously pathological
real crossover completed in 165.512s instead of about 20 minutes. Public MCP context measured 8.935s, consistent
with the targeted-reference result above.

## Disposition

- Closed locally: convergence-time JSON diagnostics.
- Closed locally: context reference SQL and bounded term-rescue performance regression.
- Closed locally: prepare-time semantic activation and Miller recovery/status behavior.
- Closed by released follow-up: real Windows source CI and julie-extract 2.32.0 recovery/performance dogfood.
- Pending from this earlier relief gate: `cargo-deny` was unavailable locally; use the existing remote
  dependency-policy result for release review.
- Recommended next step after the final Miller branch gate and separate approval: Miller `1.18.2` with the released
  julie-extract 2.32.0 pin.
- No pin, workflow, version, tag, push, publication, package, or release was changed or performed.
