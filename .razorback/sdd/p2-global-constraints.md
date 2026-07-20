## Global Constraints

- `MILLER_SEMANTIC=off` (and alias `0`) is a permanent zero-work guarantee per vectors-v1 §File placement and activation: no vectors.db open/create/stat, no `vectors.gen-*.db` enumeration, no sqlite-vec load, no child process, no GPU probe, zero added latency. Test-enforced.
- Lexical-only output stays byte-identical in every mode. Lane c proves this with golden-output tests; lane b must not alter any existing tool output when semantic is off or absent.
- No new MCP tools, no MCP parameter additions, no ServerInstructions growth (`AgentInstructionsTests` stays green). CLI-only surfaces are allowed.
- Fast suite stays fast and pure: tests needing the real sqlite-vec native extension or spawning processes are `[Trait("Category","Scale")]` and SKIP (not fail) when the extension/tooling is absent. A test spawning `julie-extract` uses `ScaleTestSupport.RequireJulieServer()`.
- Build is 0 warnings / 0 errors (`dotnet build Miller.slnx -c Release`, TreatWarningsAsErrors).
- Five generation-identity fields and their pinned initial values come verbatim from vectors-v1 §Generation identity / §Pinned initial values. `fusion_profile` never invalidates stored vectors; `reader_compatibility` never triggers re-embedding.
- The chunk cursor never advances past what content.db proves under all four chunk-cursor precondition rules (vectors-v1 §Cursors) — never a bare revision comparison.
- Telemetry fields for canary plumbing come verbatim from `canary-telemetry-v1.md` (enum/counter-only; no query text, no paths in persisted telemetry).
- Card text v1 is local-only (no graph enrichment): `{kind} {qualified name} {signature first line} {doc excerpt ≤300} in: {container} {path}`, ~1,200-char budget, word-boundary truncation, comment-marker stripping. Eligibility is symbol-kind/data-driven, never a language blocklist.
- MinHash analyzer is deterministic (fixed normalization, seeds, LSH params) and separate from `CloneGroupReader`, which is untouched. Card vectors are never used for clone claims.
- Do NOT push miller commits to origin — all work stays local per the 2026-07-20 no-push directive (commits are fine; pushes are not).

