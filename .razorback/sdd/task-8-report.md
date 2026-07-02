# Task 8 Report — Docs, Instructions, Skill Sync

**Plan:** `docs/plans/2026-07-02-backend-http-boundary-consumption.md` (Task 8, final)
**Branch:** `feat/backend-http-boundary`
**Scope:** docs + agent-instructions + skill sync + AgentInstructionsTests exact-string lockstep. No production code, no test-behavior changes.

## Status: COMPLETE (worker scope green)

## Provider list — before/after (append `backend-http` to each Oxford/slash list)

- **Before:** `` `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, and `react` ``
- **After:** `` `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http` ``

The full 8-provider list is now consistent across trace-json-v1.md, MILLER_AGENT_INSTRUCTIONS.md (3 places), miller-bridge-trace/SKILL.md, and miller-orientation/SKILL.md.

## Files changed

| File | Change |
|------|--------|
| `docs/contracts/trace-json-v1.md` | provider entry + verb_unknown arm + enrichment/csharp/NOT-claimed paragraphs + evidence keys + diagnostic nouns |
| `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` | backend-http in all 3 provider lists + fact-feed credit; trimmed 3 redundant fragments to stay under the 12000-char MCP budget |
| `tests/Miller.Tests/Server/AgentInstructionsTests.cs` | Oxford-list assertion updated; +1 fact-feed assertion |
| `.agents/skills/miller-bridge-trace/SKILL.md` | description + provider list + backend fact families/pattern audits + reading-results bullet + report list |
| `.agents/skills/miller-orientation/SKILL.md` | line 60 provider sentence → full 8-provider list |
| `skills/miller-bridge-trace/SKILL.md` | regenerated mirror (byte-identical) |
| `skills/miller-orientation/SKILL.md` | regenerated mirror (byte-identical) |

## AgentInstructionsTests.cs lines changed

- List assertion: now `Assert.Contains("`dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http`", instructions);`
- Fact-feed block: added `Assert.Contains("plus `backend-http` for python/go/java/ruby and vue client requests beyond js/ts", instructions);`
- The two pre-existing fact-feed assertions (`...facts feed `dotnet-web``, `and the `*-api` providers`) were kept unchanged — the reworded markdown still contains both substrings verbatim.

## backend-http content added to trace-json-v1.md

- **Provider scope entry** — client families, 10 route families with their `*.route.v1` pattern ids, `hits`/`route` label, three enrichment passes named.
- **verb_unknown arms** folded into the shared verb-aware paragraph: a verb-less backend handler (Express/Fastify all-method, gin/echo `Any`, method-less Spring `@RequestMapping`, every Django URLconf) → route-only Medium `verb_unknown`; equally-specific tie → no edge.
- **Enrichment semantics** (unambiguous-or-nothing): (1) mount-prefix composition anchors each `express.router_mount.v1`/`fastapi.include_router.v1`/`flask.blueprint_registration.v1`/`django.url_include.v1` to exactly ONE other file (Django by module path, others by trailing identifier), APPENDS prefixed variants, zero-or-multiple → `unanchoredMounts`, strictly additive; (2) Rails resource expansion (`resources`→8, `resource`→7, `only`/`except`, `scope_path`); (3) Rails `controller_action`/expanded-action binding to the ONE non-test controller method (collision poisons → synthesized endpoint).
- **csharp client-request contract:** a non-test csharp `HttpClient` structural `http.client_request.v1` fact is a real client call feeding `dotnet-web` (service-to-service); test HttpClient filtered by `is_test`.
- **NOT claimed:** regex-form Django url patterns (blank route honestly excluded, never synthesized), `rails.mount.v1` Rack-app/engine internals (evidence-only, never composed/bridged), non-literal/dynamic templates (upstream M2 silence).
- **Evidence-count keys** appended: `backend-http.clientRequests`, `.routeFacts`, `.mounts`, `.composedRoutes`, `.unanchoredMounts`, `.expandedResourceRoutes`, `.candidates`, `.ambiguousMatches`.
- **Route-string diagnostics:** added `backend-http_*` prefixed codes over the same 5 suffixes, nouns "client request"/"route".

## Skill diffs (summary)

- **miller-bridge-trace:** frontmatter `description` gains a backend-http clause; provider list gains the `backend-http` entry; HTTP-boundary audit intro adds `backend-http` and the code block gains `express.route.v1`, `fastapi.route.v1`, `rails.resource_route.v1`; a Reading-Results bullet on backend-http verb/mount/Rails/NOT-claimed semantics; Report "Provider assumption" list gains `backend-http`.
- **miller-orientation:** line 60 sentence `dotnet-web, nextjs, nuxt, vue, react` (doubly stale — also missing `nextjs-api`/`nuxt-api`) → full `dotnet-web, nextjs, nextjs-api, nuxt, nuxt-api, vue, react, backend-http`.

## cmp results (byte-identical mirror)

- `cmp -s skills/miller-bridge-trace/SKILL.md .agents/skills/miller-bridge-trace/SKILL.md` → MATCH
- `cmp -s skills/miller-orientation/SKILL.md .agents/skills/miller-orientation/SKILL.md` → MATCH
- Regenerated via `scripts/sync-plugin-skills.sh`. git status shows only the two edited SKILL.md files changed under `skills/`.

## CLAUDE.md grep (proves no edit needed)

`grep -nE "dotnet-web|nextjs|nuxt|provider-scoped|mode=bridge" CLAUDE.md` → exit 1 (no matches). CLAUDE.md does not enumerate bridge providers, so no CLAUDE.md edit and no `scripts/sync-agents.sh` run.

## Verification (worker scope)

Pre-commit HEAD `33e8fb6`, 2026-07-02T21:24Z:

| Invariant | Command | Result |
|-----------|---------|--------|
| markdown ↔ exact-string assertions agree | `dotnet test … --filter "FullyQualifiedName~AgentInstructionsTests&Category!=Scale"` | 41 passed, 0 failed |
| full fast suite (incl. skill/content guards) stays green | `scripts/test.sh` | 2694 passed, 0 failed, 15s |
| 0 warnings / 0 errors (warnings-as-errors) | `dotnet build Miller.slnx -c Release` | Build succeeded, 0 Warning(s), 0 Error(s) |
| skill mirror byte-identical | `cmp -s skills/<n>/SKILL.md .agents/skills/<n>/SKILL.md` | both MATCH |

MCP instruction budget: markdown is 11963 CRLF-normalized chars (<12000, 37 slack).

Scale suite NOT run — LEAD's branch-gate responsibility per the task.

## Miller calls used

- `inspect(target="BackendHttpBridgeProvider", depth=full)` — confirmed the 8 evidence-count key strings emitted by `BuildCandidates` (`src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:118-127`) and the composition/expansion/binding behavior documented.
- `patterns` schema loaded but not needed (pattern ids read from plan + provider source).
- Cross-checked the TraceTool diagnostic row via `grep` on `src/Miller.Server/Tools/TraceTool.cs:1027` — `new("backend-http", "Backend", "client request", "route", …)` confirms nouns "client request"/"route" (NOT "route fact"; the Task 6 doubling fix).

## API-shape evidence (where each evidence-count key came from)

All 8 keys read verbatim from the `evidenceCounts` dictionary in `BackendHttpBridgeProvider.BuildCandidates` (BackendHttpBridgeProvider.cs:118-127): `clientRequests`=clientRequests.Count, `routeFacts`=backendRoutes.Count, `mounts`=mountFacts.Count+railsMountCount, `composedRoutes`=composition.Composed.Count, `unanchoredMounts`=composition.UnanchoredMounts, `expandedResourceRoutes`=expandedResourceRoutes.Count, `candidates`=result.Edges.Count, `ambiguousMatches`=result.AmbiguousMatches. Not from memory.

## Judgment calls

- `MILLER_AGENT_INSTRUCTIONS.md:69 - removed "(attribute + minimal-API routes)" over keeping it because` that dotnet-web detail is fully covered in trace-json-v1.md and the parenthetical was pure budget cost; the required `feed `dotnet-web`` pin is unaffected.
- `MILLER_AGENT_INSTRUCTIONS.md:74 - shortened the `mode=path` no-path sentence to "is not proof unrelated" over the long form because` the pinned phrase "no extracted graph path within depth, not proof unrelated" already lives on the trace-tool bullet (line 39) and "follow its `Next:` actions" is retained; this reclaimed budget without dropping any pinned string.
- `MILLER_AGENT_INSTRUCTIONS.md:30 - deleted "Multi-file ambiguity includes copyable scoped reruns." over keeping it because` it is unpinned secondary detail and the 12000-char MCP budget left only ~37 chars of slack once backend-http was added to 4 places; documenting the shipped provider took priority.
- `AgentInstructionsTests.cs - kept the two existing fact-feed assertions and added ONE backend-http assertion over rewriting all three because` the reworded markdown still contains both original substrings verbatim, so the minimal lockstep change is the safest.
- `MILLER_AGENT_INSTRUCTIONS.md fact-feed - credited only python/go/java/ruby+vue (not csharp) as the new backend-bridging client languages over listing all new languages because` csharp's HttpClient is Task 5's dotnet-web service-to-service contract, not a backend-http family; trace-json-v1.md documents the csharp→dotnet-web path separately.
