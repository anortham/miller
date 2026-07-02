# Task 6 — Trace Surface: Diagnostics, Evidence Keys, Render

**Status:** COMPLETE (green). Branch `feat/backend-http-boundary`.

Wires the `backend-http` bridge provider into the `trace` tool's hardcoded diagnostic
lists, evidence-key checks, next-actions, and provider-list docstrings. Purely additive
wiring into existing hardcoded lists — no new abstractions, no render-path changes.

## Exact edits (`src/Miller.Server/Tools/TraceTool.cs`)

1. **`FileRouteDiagnosticProviders` array (~:1024):** added
   `new("backend-http", "Backend", "client request", "route", BridgeNodeKind.Endpoint, "routeFacts", "route edge")`
   after the `react` row, mirroring the `nextjs-api`/`nuxt-api` Endpoint rows. Added a comment
   noting the single-key `routeFacts` participation gate and the intentional Rails-only-expanded
   limitation (do NOT OR composed/expanded counts into the gate).
   **Lead-review fix:** `TargetFactName` is `"route"` (the fact SUBJECT), NOT `"route fact"` — the
   diagnostic templates (`:975/983/991/999/1005`) append `" fact"`/`" facts"`/`"s"` to it, matching
   the existing `"file route"`/`"route handler"`/`"server route"` convention. `DefinitionEvidenceName`
   stays the `"routeFacts"` evidence key.

2. **`HasRouteFactEvidence` (~:1752):** appended the four backend route-side keys
   `backend-http.routeFacts | .clientRequests | .composedRoutes | .expandedResourceRoutes`
   so the generic route-fact audit fires for backend repos.

3. **Shared HTTP-client-request next-action (~:1668):** added
   `|| HasEvidence(capabilityReport, "backend-http.clientRequests")` to the existing
   dotnet-web/nextjs-api/nuxt-api OR, so the `http.client_request.v1` audit fires for backend repos.

4. **New backend route-fact audit block (~:1719):** when
   `backend-http.routeFacts | .composedRoutes | .expandedResourceRoutes` present, adds a `patterns`
   next-action with `("operation","search"), ("query","route")` and reason
   `"audit backend HTTP route structural facts consumed by the backend-http bridge"` (the honest
   generic pointer — 10 backend families share no single pattern_id; `query=route` matches the
   `.route.` families and the generic route audit covers the rest).

5. **Provider-list docstrings** — updated to the full 8-provider list:
   - class/`mode=bridge` doc header (`:31`): `…vue, react, and backend-http.`
   - MCP `[Description]` (`:61`): `…vue, react, backend-http) with a confidence band.`
   - inline `FileRouteDiagnosticProvider` comment (`:149`): now `nextjs-api/nuxt-api/backend-http`
     compare client requests against Endpoint `handler/route-fact` observation nodes.
   - **Budget trim:** the `[Description]` was 894/900 chars; `+", backend-http"` (=908) would trip
     `ToolDescriptions_StayWithinClaudeCodeBudgets`. Trimmed the redundant `caller/callee ` from
     "manual caller/callee file hopping" → "manual file hopping" (−14), landing back at 894. No test
     references that phrase; meaning preserved (callers/callees are already described by `mode=auto`).

## Not changed (as instructed)
- Render path untouched. Backend edges flow through `BridgeKind.Hits` → label `route` (`:1397`),
  JSON kind `hits` (`:2243`). Asserted, not modified.
- `MILLER_AGENT_INSTRUCTIONS.md` and skills NOT touched (that is Task 8). Its provider list
  (guarded by `AgentInstructionsTests.Load_DocumentsTraceRecoveryGuidance`) still reads the 7-provider
  list — left for Task 8. My `[Description]` edit does not feed `AgentInstructions.Load()`, so that
  guard is unaffected.

## Tests added (`tests/Miller.Tests/Tools/TraceToolTests.cs`) — through `TraceTool.Run`
- `Bridge_RouteStringTarget_BackendHttpClientRequestOnly_JsonExplainsNoRouteFactMatch` — acceptance
  fixture: express route fact `/api/orders` + client request to unmatched `/api/users`. Asserts
  `backend-http_route_no_file_match` with "Backend client request exists: /api/users" and
  "observed route facts: /api/orders". **Invariant:** unmatched backend client request surfaces a
  backend-http-scoped diagnostic with the correct DisplayName/ReferenceNoun/TargetFactName nouns.
- `Bridge_RouteStringTarget_BackendHttpBothFactsNoEdge_JsonUsesRouteEdgeNoun` — both facts for
  `/api/users`, no edge → `backend-http_route_no_bridge_link`, "no route edge was built".
  **Invariant:** the EdgeNoun "route edge" renders per-provider for backend-http.
- `Bridge_NotOnBridge_WithBackendHttpEvidence_OffersBackendRouteAndClientRequestAudits` (compact) —
  routeFacts+clientRequests+expandedResourceRoutes. **Invariant:** generic route audit + shared
  `http.client_request.v1` audit + backend route-fact audit all fire for a backend repo.
- `Bridge_NotOnBridge_WithBackendHttpEvidence_JsonCarriesBackendRouteAndClientRequestAudits` (JSON) —
  composedRoutes-only (no direct routeFacts) + clientRequests. **Invariant:** the composed-route key
  opens the route-fact gate and fires the backend audit (matched by reason string); client-request
  key fires the `http.client_request.v1` audit.
- `Bridge_BackendHttpClientRequestEdge_CompactAndJsonAgreeOnKindLabelBandAndFlags` — matched High
  Hits edge. **Invariant:** compact ("--route-->", "0.90 (High)", no flags) and JSON
  (kind=hits, label=route, confidence=high, score=0.9, empty flags) AGREE for a backend edge.
  (Guard test — green before and after impl, since the render path is unchanged.)

TDD: 4 diagnostic/next-action tests written first, watched fail (route codes were generic
`route_no_backend_match`/`route_no_bridge_link`; audits absent), then implemented, watched pass. The
render-agreement test was green from the start (asserts unchanged behavior).

## API-shape evidence (confirmed against Miller code before use)
- `FileRouteDiagnosticProvider` record field order (`:158`):
  `(ProviderId, DisplayName, ReferenceNoun, TargetFactName, DefinitionNodeKind, DefinitionEvidenceName, EdgeNoun)`
  — matches the plan tuple exactly.
- `BridgeNodeKind.Endpoint` — the API/backend definition node kind; `ProviderParticipates` (`:1025`)
  gates Endpoint providers on `FileRouteEvidenceCount(graph, providerId, DefinitionEvidenceName) > 0`
  = `backend-http.routeFacts > 0`.
- Evidence-key strings verified against the emitter
  `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:109-119`:
  `backend-http.clientRequests`, `.routeFacts`, `.composedRoutes`, `.expandedResourceRoutes` (plus
  `.mounts/.unanchoredMounts/.candidates/.ambiguousMatches`) — exact match to plan Task 2/3/4.
- Diagnostic-code helper `FileRouteDiagnosticProvider.DiagnosticCode(suffix) => ProviderId + "_" + suffix`
  (`:167`) → codes are `backend-http_route_no_file_match`, `backend-http_route_no_bridge_link`, etc.
- Next-action helper `NextAction(string tool, string reason, params (string,string)[] args)` (`:1746`);
  compact render appends ` - {reason}.` (`:1762`), JSON writes `tool`/`reason`/`args` (`:2034`).
- Render: `BridgeKind.Hits => "route"` label (`:1397`), `BridgeKind.Hits => "hits"` JSON kind (`:2243`).

### Miller/codebase calls used and what each confirmed
- `inspect(mcp__miller__inspect)` schema loaded via ToolSearch (orientation).
- Read `TraceTool.cs` regions (`:1..80`, `:140..169`, `:813..1006`, `:1008..1099`, `:1620..1749`) —
  confirmed record shape, `ProviderParticipates` gate, diagnostic orchestration
  (`TryBuildRouteDiagnostic` → `TryBuildFileRouteDiagnostic` per-provider, generic fallback), and the
  next-actions builder.
- Read `TraceToolTests.cs` (`:1..130`, `:1128..1170`, `:1620..1710`, `:2237..2444`) — confirmed
  `BuildBridgeIndex`/capability harness and the nextjs-api/nuxt-api diagnostic + next-action precedents.
- Read `AgentInstructionsTests.cs` (`:30..272`) — confirmed the trace `[Description]` guards
  (`TraceToolDescription_DocumentsRecoveryGuidance` substrings unaffected;
  `ToolDescriptions_StayWithinClaudeCodeBudgets` ≤900 → trim needed) and that the 7-provider list
  guard reads `AgentInstructions.Load()` (the instructions DOC), not the trace `[Description]`.
- `grep BackendHttpBridgeProvider.cs` — confirmed the evidence-key emitter and `ProviderId = "backend-http"`.

## Self-review findings / judgment calls
- **Acceptance "e.g." diagnostic codes are illustrative.** The plan lists
  `route_no_reference_match / route_no_bridge_link / route_not_observed` as examples; the acceptance
  fixture (client request to a route with NO matching route fact) actually produces
  `backend-http_route_no_file_match` (reference present, definition absent). The load-bearing
  criterion — "backend-http-scoped diagnostic with the correct nouns" — is met. Added a second test
  (`route_no_bridge_link`) so the EdgeNoun "route edge" is also exercised.
- **Noun fix (lead review):** the plan's illustrative `("Backend", "route fact")` tuple didn't account
  for the templates appending `" fact"`/`" facts"`/`"s"` to `{TargetFactName}`, which produced doubled
  output ("route fact fact", "route fact facts"). Changed `TargetFactName` to `"route"` (the subject),
  matching the existing rows. Rendered noun strings now produced for backend-http:
  - `route_no_file_match`: "Backend client request exists: {route}; no matching route fact. observed routes: {…}"
  - `route_no_bridge_link`: "Backend client request and route facts exist for {route}, but no route edge was built for that route."
  - `route_no_reference_match`: "Backend route exists: {route}; no matching client request fact. observed client requests: {…}"
  - `route_not_observed`: "no Backend client request or route facts observed for {route}."
  - `route_ambiguous_file_match`: "Backend client request exists: {route}; multiple matching route facts were observed, so no route edge was built. observed routes: {…}"
- **Wall-clock tripwire (cold run):** first `scripts/test.sh` reported 35s wall (dotnet test duration
  28s, under the 30s ceiling) — cold Release build overhead. Warm re-run: 14s wall / 10s test. All
  2694 fast tests pass; no slow test leaked (5 new tests are pure in-memory, ~50ms total).
- **Commit scope:** `.razorback/sdd/task-{1,3,4}-report.md` and the plan doc were already dirty at
  session start (prior-task work); left untouched. This `task-6-report.md` held a stale report from an
  older plan; overwritten to match the per-plan reuse convention already applied to task-1/3/4.

## Verification
- Invariant proven: backend-http participates in route diagnostics, evidence gates, and pattern
  audits identically to the existing Endpoint providers, and backend edges agree across compact/JSON.
- Command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests&Category!=Scale" -v minimal`
  → **Passed 85 / Failed 0** (was 80; +5).
- Ceiling: `scripts/test.sh` → **Passed 2694 / Failed 0** (warm 14s wall / 10s test, under budget).
- Build: `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)**.
- Timestamp: 2026-07-02. Scale suite NOT run (Task 7).
