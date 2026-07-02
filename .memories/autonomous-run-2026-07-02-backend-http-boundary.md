# Autonomous Execution Report — Backend HTTP Boundary Consumption

**Status:** Complete
**Plan:** docs/plans/2026-07-02-backend-http-boundary-consumption.md
**Branch:** feat/backend-http-boundary (off main @ 24f46fc)
**PR:** not created — local commits only, per the user's standing no-push/no-release constraint for this work. Branch is ready for the user to push/PR when they choose.
**Duration:** one session (subagent-driven-development, sequential dispatch)
**Phases/Tasks:** 8/8 tasks complete

## What shipped
Miller's `trace mode=bridge` now resolves client HTTP requests to server route handlers across **16 new backend structural-fact families** (Express, Fastify, FastAPI, Flask, Django, Spring, Go net/http, gin, echo, Rails + their mounts), consuming julie-extract 2.7.0 `structural_facts` via the `normalized_route_template` join key.

- **T1 `d731506`** — 16 pattern-id constants + `BridgeFactPatternIds` SQL whitelist + `BackendRoutePatternIds`; `TryReadBackendRoute` (effective-template precedence, Spring `class_route` reject, nullable UPPERCASE verb, blank/Django-regex honest exclusion) + `TryReadMountFact`/`StructuralMountFact`.
- **T2 `64fa4d9`** — standalone `BackendHttpBridgeProvider` (direct client→route joins via `FileRouteBridge`/segment matcher); registered in `BridgeGraphBuilder.DefaultProviders` + `BridgeProviderSelection`.
- **T3 `7124299`** — cross-file mount-prefix composition, **unambiguous-or-nothing** (Django by included-module path; Express/FastAPI/Flask by mount-target trailing identifier); strictly additive; `unanchoredMounts` counted.
- **T4 `9fe3b5c`** — Rails resource expansion (8 collection / 7 singular entries, `only`/`except`, `scope_path`) + `controller_action` binding to the one non-test controller method symbol (collision poisons to a synthesized endpoint).
- **T5 `144f6e2`** — narrowed `RouteBridge.IsRealClientCall` csharp exclusion to `AttestedVerb is null`, admitting csharp `HttpClient` structural requests as real client calls into `dotnet-web` (service-to-service); test-project calls stay filtered by `is_test`.
- **T6 `506374d` + `199a2db`** — wired `backend-http` into TraceTool route-string diagnostics, evidence gates, next_actions, and docstrings; lead-fixed a noun-doubling bug (`"route fact"` → `"route"`).
- **T7 `33e8fb6`** — 7 live polyglot scale tests + the parity gate proving all 16 families and 8 client languages emit AND bridge on a real julie-extract 2.7.0 extract.
- **T8 `2869c79`** — documented the provider across trace-json-v1.md, MILLER_AGENT_INSTRUCTIONS.md, and both skills; `AgentInstructionsTests` exact strings updated in lockstep; skills mirror regenerated.

## Judgment calls (non-blocking decisions made)
- `tests/.../LiveBridgeTraceTests.cs` (T5 fixture) — used the parameterized `/api/users/{id}` route over the plan's illustrative bare-numeric `/api/users/42`, because dotnet-web's `RouteBridge.Resolve` uses exact canonical-route equality (folds `{p}`/`:p`/`${p}`→`{}` but not bare numerics), unlike backend-http's segment matcher. The plan example wouldn't have matched.
- `src/Miller.Server/Tools/TraceTool.cs` (T6) — `FileRouteDiagnosticProvider.TargetFactName = "route"` not the plan's illustrative `"route fact"`, because the diagnostic templates append `" fact"`/`" facts"`; the example would have rendered "route fact fact". Matches the existing `"file route"`/`"server route"` convention.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (T8) — trimmed 3 genuinely-redundant fragments (a duplicated `mode=path` phrase kept at its canonical bullet, a dotnet-web parenthetical covered in trace-json, one scoped-rerun note) to stay under the 12000-char MCP budget (landed 11895). No pinned assertion string lost — verified by re-running AgentInstructionsTests 41/41.
- `.agents/skills/` provider ordering (T8) — appended `backend-http` at the end of each Oxford-comma list (cleanest for the "X, Y, and Z" grammar and the exact-string test) rather than grouping it beside dotnet-web.

## External review (lead inline, adversarial)
No separate reviewer subagent (subagent-driven-development uses lead inline review). Every task got a full lead inline review (spec compliance + code quality + Miller-first evidence check) before approval.
- **T3** received an explicit **adversarial** review as the concentrated-risk task: the ambiguity-poisons guarantee (anchors return `string?`, compose only in the non-null branch) was confirmed structurally sound and pinned by negative tests.
- **T6** review **caught + fixed** the noun-doubling defect (`199a2db`).
- **T7** (hard gate) review confirmed the parity gate uses explicit 16-family + 8-language lists with per-element `Assert.Contains` (a dropped/renamed family fails loudly), the honesty doctrine is proven live (django/gin/Spring → Medium `verb_unknown`; verb-attested → High), Rails expansion binds to the real `UsersController#show` symbol, `rails.mount` stays evidence-only, and no assertion was softened.
- **T8** review verified the instruction trims were redundant (lead re-ran AgentInstructionsTests independently) and the 8-provider list is consistent across all 4 surfaces.

## Tests
- Fast suite (`scripts/test.sh`): **2694 passed / 0 failed** (14s, under the 30s tripwire).
- Scale suite (`scripts/test.sh scale`, spawns real julie-extract 2.7.0): **45 passed / 0 failed** (38 baseline + 7 new backend-http live tests).
- Release build (`dotnet build Miller.slnx -c Release`): **0 warnings / 0 errors**.
- Skills mirror (`cmp -s skills/ .agents/skills/`): **MATCH** for both skills.
- No upstream (STOP-and-report) findings: every claimed 2.7.0 family and client language emitted live and bridged; nothing softened.

## Blockers hit
- None.

## Files changed
22 files, +4035 / −283 (main..HEAD): `BackendHttpBridgeProvider.cs` (+673, new), `BridgeGraphBuilderTests.cs` (+1653), `LiveBridgeTraceTests.cs` (+688), `TraceToolTests.cs` (+244), `StructuralRouteFactAdapter.cs` (+116), `BridgeStructuralPatterns.cs` (+57), `RepositoryIndexLoaderBridgeTests.cs` (+68), `trace-json-v1.md` (+59), `TraceTool.cs`, `RouteBridge.cs`, both SKILL.md mirrors, `MILLER_AGENT_INSTRUCTIONS.md`, `AgentInstructionsTests.cs`, registration files.

## Next steps
- **Push / PR is the user's call** — the branch is complete and gate-green locally; nothing has been pushed, tagged, or released (per the standing constraint). When ready: `git push -u origin feat/backend-http-boundary` then open a PR against `main`.
- No follow-up work is required by the plan. All 8 acceptance-criteria blocks are ticked.
