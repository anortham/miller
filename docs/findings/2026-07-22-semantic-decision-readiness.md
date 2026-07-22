# Semantic decision canary readiness

**Date:** 2026-07-22  
**Frozen runtime:** `dc12001c9159dd3cd06edcd11a81bd7454e6e7c8` (`1.13.0+dc12001c9159`)  
**Verdict:** ready to collect decision evidence; not mature and not approved for default-on. The visible replay
shows enough retrieval value to justify the bounded canary, while the online and sealed task gates are still
underpowered or unrun.

## Frozen install and cohort

- Executable: `/Users/murphy/.local/share/miller/canary/semantic-decision-dc12001/miller`
- Manifest: `/Users/murphy/.local/share/miller/canary/semantic-decision-dc12001/MANIFEST.md`
- `miller` SHA-256: `a9cf57ef4dabe0c0cb296c903e1786ed252faa92d3e2b9371147a2607fe00b4c`
- `miller.dll` SHA-256: `21847d0fdedcc841c44899f867b9ccccc66c720fa255f7f120dfc2ea7c6761c9`
- `julie-extract` 2.16.0 SHA-256: `4c26c1bef6df8dd1cc4d274d46228ae3246efa78e67e75b99bffae3185e6e70d`
- `julie-semantic-sidecar` 0.1.0-rc.2 SHA-256: `334455f9fb22bf4b2e4f9d90add708e3da81f9dc6eff1d941e4c658b1252a161`
- sqlite-vec 0.1.9 SHA-256: `193e480c50b59a55977d166f4aaf0e1bc8832d6963516e5950f39e4d2ce0b793`
- Source id: `87e21b3bfc0a9e3e720b51abffaa1b00`
- Start: 2026-07-22; day 14: 2026-08-05; day 30 hard stop: 2026-08-21.

The live artifact identity matches the frozen BGE cohort: encoder
`sha256:3e8b7e8a0890dc84f702db1d13c47e312501905ee9d1aafb772bdc803616d7f4`, storage
`vec0-int8-384-cosine-v1`, corpus `cards-v1-chunks-v1`, fusion `fusion-v1`, policy 1. Both vector cursors are at
revision 308 with zero pending files or errors; the active artifact id is
`artifact-1784670037520138000`.

## Local enrollment

The existing global Miller entries in Codex, Claude, and Cursor now resolve the immutable executable with
`MILLER_SEMANTIC=on` and `MILLER_SEMANTIC_CANARY=decision`:

- `/Users/murphy/.codex/config.toml`
- `/Users/murphy/.claude.json`
- `/Users/murphy/.cursor/mcp.json`

`/Users/murphy/.claude/settings.json` has no MCP server registry and was intentionally left byte-untouched. An
unrelated project-local legacy entry under the Moltbot project in `.claude.json` was also left untouched. Config
changes take effect when each client next restarts its Miller process; the readiness probe launched the frozen
server explicitly and stopped only that probe.

Rollback restores the three global entries to
`/Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller` with semantic `on` and canary `on`,
then restarts the clients. `MILLER_SEMANTIC=off` remains the immediate zero-work safety switch.

## Live smoke and telemetry

- Model prepare found the verified cached BGE model at SHA-256
  `bf40c42ad7d89382e9ba7376d5c4b73f6b556cb541fab37aaa1da9c320149b65`.
- Forced lexical identifier search returned `FullRebuildPromotion` first. Normal identifier canary calls served
  lexical output and five v3 shadows reported zero top-1 changes, overlap-at-10 8, and lexical top-1 rank 1.
- The prose query `where is the full rebuild promoted atomically` returned `FullRebuildPromotion` first on the
  production route. Its five-call unit randomized to control, so it proves holdout operation but supplies no
  treatment-value evidence yet.
- Contract-v3 export produced one visible control unit and one visible shadow unit with no suppression. The
  local v3 gate correctly reported success underpowered (1 control, 0 treatment), latency indeterminate
  (0 warm treatment, 5 control), and identifier shadow underpowered (1 unit).
- Contract-v2 export and gate remain separately readable (2 visible units, 5 suppressed units, 5 cohorts in the
  checked window); v2 data was not pooled into v3.

## Cost and reliability screen

| Check | Evidence | Limit | Result |
| --- | ---: | ---: | --- |
| frozen sidecar ready RSS | 191,360 KiB (186.9 MiB) | at most 256 MiB | pass |
| three active sidecars aggregate RSS | 373,120 KiB (364.4 MiB) | at most 768 MiB | pass |
| five-minute ready idle CPU | 0.3%, 0.1%, 0.1%, 0.1%, 0.0%; mean 0.12% | below 1% | pass |
| clean Miller converge peak | 485,744 KiB (474.4 MiB) | at most 600 MiB | pass |
| clean Miller vector converge | 34 s for 10,888 units | at most 60 s for at most 15,000 | pass |
| current artifact | 10,347 symbol + 850 chunk vectors; 9,990,144-byte active DB | observation | recorded |

The converge peak/time rows reuse the real pinned-BGE clean-corpus measurement in
[`2026-07-21-semantic-production-readiness-evaluation.md`](2026-07-21-semantic-production-readiness-evaluation.md).
The frozen candidate uses the same encoder/sidecar/storage lane; the exact-candidate replay independently
converged both clean corpora with zero cursor errors. Warm canary latency remains unpowered, and the cold CLI
replay's roughly 4x p95 is diagnostic rather than the warm gate.

## Verification ledger

| UTC time | Scope | Command/evidence | Result |
| --- | --- | --- | --- |
| 2026-07-22 13:34 | exact frozen commit | `dotnet build Miller.slnx -c Release` | pass, 0 warnings/errors |
| 2026-07-22 13:35 | exact frozen commit | `scripts/test.sh` | pass, 4,551; skip, 2; fail, 0 |
| 2026-07-22 13:36 | exact frozen commit | `scripts/test.sh scale` | pass, 87; skip/fail, 0 |
| 2026-07-22 13:34 | evaluator | `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj` | pass, 66 |
| 2026-07-22 13:34 | live-arm runner | `python3 -m unittest eval/retrieval-eval/tests/test_run_live_arm.py` | pass, 4 |
| 2026-07-22 13:38 | install | version and SHA-256 checks against immutable copy | pass |
| 2026-07-22 13:40 | vectors | `workspace status --json` and `workspace health --json` | ready, both cursors 308/308 |
| 2026-07-22 13:42 | MCP smoke | initialized frozen stdio server and issued ten real `search` calls | pass |
| 2026-07-22 13:43 | telemetry | v3 export and local v3 gate; legacy v2 export/gate | shapes valid; v3 underpowered |
| 2026-07-22 13:44–13:48 | operations | five one-minute ready-state sidecar samples | resource limits pass |

The visible baseline at [`2026-07-22-semantic-decision-baseline.md`](2026-07-22-semantic-decision-baseline.md)
showed production versus lexical gains of +0.1528 recall@10 and +0.1379 nDCG@10, three zero-result rescues,
no lost relevant query, and identical ranked lists for all 16 identifier queries. That is strong evidence not to
remove semantics before the bounded decision, but it is not task-completion or causal proof.

## Remaining promotion blockers

- Accumulate at least 30 control and 30 treatment units, 100 warm treatment and 100 control rows, and 30
  identifier-shadow units without changing the frozen cohort identity.
- Run the user-owned blinded sealed task event and return only its aggregate; the primary completion and
  identifier/path safety clauses must pass.
- Take non-overlapping weekly UTC exports after the 600-second attribution horizon and force a promote/remove
  verdict on 2026-08-21. The exact schedule and removal scope are in the
  [operator runbook](2026-07-21-p5-canary-runbook.md).
