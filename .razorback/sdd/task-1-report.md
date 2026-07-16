# Task 1 — Anti-flicker: idiomorph morph swaps + fragment ETag/304

**Status: DONE** — blocker resolved by lead ruling (Option A). All acceptance criteria met, all boxes ticked,
committed. See *Fix round — Option A* at the end for the resolution, new tests, and final ledger.

> The *Blocker* section below is preserved as the historical record of why criterion 1 could not be met as
> originally specified, and the evidence that drove the ruling. It is no longer an open blocker.

- Worktree: `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
- Branch: `worktree-dashboard-ux-fixes` (fix round committed on top of base `f8dec96`; SHA reported to lead —
  a commit cannot cite its own hash, so the ledger says "this commit")
- Dirty: clean after commit (13 files committed; inventory below)

> Note: the previous contents of this path were a stale report from an unrelated run
> (*"Page-spine resilience + endpoint parity"*, worktree `.worktrees/dashboard-polish`, branch
> `feat/dashboard-polish`). Read and confirmed unrelated before overwriting.

---

## Blocker — the plan's antiforgery assumption is false

The plan states: *"Antiforgery tokens are deterministic per cookie, so hashes are stable across polls for the
same client… If the token proves non-deterministic in the test, STOP and report — do not strip tokens from
fragments."*

**The token is non-deterministic.** Proven, not inferred. Two back-to-back `GET /fragments/workspaces` with the
**same** antiforgery cookie return bodies that are **byte-identical once the token value is masked**:

| Probe | Result |
|---|---|
| Raw bodies equal | `False` |
| Bodies equal with `__RequestVerificationToken` value masked | `True` |
| Differing regions in full diff | token value only (1 occurrence) |
| Shared token prefix | 26 chars of 155 |

The 26-char shared prefix is the Data Protection key-id header (`CfDJ8…` — same cookie, same key); the
remaining 129 chars differ. This is ASP.NET Core Data Protection: `Protect()` uses a fresh random IV/subkey per
call, so identical plaintext yields different ciphertext **every render**. It is non-deterministic *by design*
and cannot be made stable by carrying cookies — which the test does correctly.

Consequence: acceptance criterion 1 (*repeat `/fragments/workspaces` with `If-None-Match` yields 304*) is
**unachievable as specified**. Each render mints a new token → new body → new hash → always 200.

### Blast radius is exactly one fragment

`WorkspaceIndex` is the **only** polled fragment that embeds tokens (via two `WorkspaceRemoveConfirm` +
one direct `AntiforgeryToken`). Verified: `ActivityFeedPanel`, `TelemetryPanel`, `DashboardContent` embed none.

**The middleware itself is proven correct** — `FragmentActivity_RepeatWithMatchingIfNoneMatch_Returns304WithEmptyBody`
passes. So the 5s activity poll (the *most* flicker-prone surface) already gets full ETag/304 today.

### Decision needed (recommendation: A)

- **A — Hash a token-normalized body, salted with the antiforgery cookie value.** Mask
  `name="__RequestVerificationToken" value="…"` before hashing, and fold the cookie value into the hash input.
  Tokens are **not stripped** — the 200 body still ships real tokens. A 304 retains the previously delivered
  DOM *and its token*, which stays valid because ASP.NET antiforgery tokens are not single-use (validity is
  tied to the cookie, not a per-render nonce). Salting with the cookie closes the one real edge case: if the
  cookie rotates, the ETag changes → forced 200 → fresh matching token, so a retained-stale-token 400 cannot
  happen. Cost: one scan per fragment body. Meets the criterion as written.
- **B — Zero-risk fallback:** leave `/fragments/workspaces` un-ETagged; keep 304 for activity/telemetry. Morph
  alone already removes the visible workspaces flicker. Criterion 1 would need rewording to name the fragments
  that carry no tokens.
- **C — Structural:** move remove-confirm forms out of the polled fragment (larger; touches Task 5 surface).

I did not implement A unilaterally: the plan explicitly reserved this decision, and it touches the antiforgery
surface (security-adjacent).

---

## What I implemented (complete and green)

- **Vendored idiomorph `0.7.4`** → `src/Miller.Dashboard/wwwroot/lib/idiomorph/idiomorph-ext.min.js`
  - 10,153 bytes; starts `var Idiomorph=function(){"use strict";…`
  - `sha256 a6437e55b1b6a07bc421f0d230266a39399b6826c6ed19e0ed9c63b707444a5f`
  - Verified in-file: `htmx.defineExtension("morph"` and `morph:outerHTML` handling — matches the contract
    input exactly; no idiomorph API coded from memory.
- **ETag middleware** (`DashboardHostPipeline.FragmentETagAsync`) — wraps only `GET /fragments/*`, buffers the
  body, SHA-256 → strong `ETag`, matching `If-None-Match` → 304 with empty body and no `Content-Type`.
  Non-200 replays untouched. Inline `app.Use` + small private static method, matching the existing
  exception-wrapper style. No new projects/abstractions.
- **Static route** for `/lib/idiomorph/idiomorph-ext.min.js` via the existing explicit per-file `MapMethods`
  pattern (no `UseStaticFiles`).
- **`DashboardScripts.razor`** — plain script tag, loaded before Alpine, after htmx (`<head>`).
- **Morph opt-in** — `hx-ext="morph"` + `hx-swap="morph:outerHTML"` on `#workspace-index`,
  `#activity-feed-panel`, `#telemetry-panel`, and the telemetry Refresh button.
- **Persist-open contract** — `WorkspaceRemoveConfirm` `<details data-issue-details data-issue-id="remove-@WorkspaceId">`.
- **Client JS** (`dashboard-site.js`) — module-level `fragmentETags` map keyed by element id; `htmx:configRequest`
  attaches `If-None-Match`; `htmx:afterOnLoad` stores the response ETag; `htmx:beforeSwap` turns 304 into
  `shouldSwap = false`.
- **Alpine** (`alpine-components.js`) — extracted `rehydrate()` from `init()` and bound it to `htmx:afterSwap`
  on `this.$el`, since with morph the component instance survives and `init()` no longer re-fires.

### Non-obvious constraint found (worth the lead's attention)

htmx 2's default `responseHandling` swaps **any `3xx`** (the `[23]..` rule). So an unguarded 304 would swap the
panel away with its **empty body** — blanking the panel. The `htmx:beforeSwap` guard is therefore
**load-bearing correctness, not an optimization**. Noted in-code at `dashboard-site.js`.

---

## Verification ledger

| Invariant proven | Scope | Command | Commit | Result | Timestamp |
|---|---|---|---|---|---|
| Morph/ETag contract; 2 red tests encode the blocked criterion | worker-red-green (focused) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "(Category!=Scale)&(FullyQualifiedName~DashboardFragmentCaching)"` | none | **11 passed, 2 failed** | 2026-07-16 |
| No regression across the fast suite | worker-red-green | `scripts/test.sh` | none | **3514 passed, 2 failed** (the same 2) | 2026-07-16 |
| 0 warnings / 0 errors (warnings are errors) | worker ceiling | `dotnet build Miller.slnx -c Release` | none | **Build succeeded, 0 Warning(s), 0 Error(s)** | 2026-07-16 |

Red (both encode acceptance criterion 1, unachievable per *Blocker*):
`FragmentWorkspaces_RepeatWithMatchingIfNoneMatch_Returns304WithEmptyBody`,
`FragmentWorkspaces_SameContentPolledTwice_ProducesTheSameETag`.

I left these **red rather than deleting or weakening them** — they are the honest encoding of the approved
criterion, and they turn green the moment option A lands. No scale/branch gates run (not my scope).

TDD followed: full file written first, watched fail (10 failed / 2 passed — the 2 passing were legitimately
already-true: the vendored-file check and the not-ETagged negative), then implemented to green.

---

## Files changed

| File | Change |
|---|---|
| `wwwroot/lib/idiomorph/idiomorph-ext.min.js` | **new** — vendored 0.7.4 |
| `DashboardHostPipeline.cs` | ETag middleware + idiomorph static route |
| `Components/DashboardScripts.razor` | idiomorph script tag before Alpine |
| `Components/WorkspaceIndex.razor` | morph opt-in |
| `Components/ActivityFeedPanel.razor` | morph opt-in |
| `Components/TelemetryPanel.razor` | morph opt-in (section + Refresh button) |
| `Components/WorkspaceRemoveConfirm.razor` | `data-issue-details` + `data-issue-id` |
| `wwwroot/js/dashboard-site.js` | ETag store, conditional request, 304 no-swap |
| `wwwroot/js/alpine-components.js` | `rehydrate()` on `htmx:afterSwap` |
| `tests/.../DashboardFragmentCachingTests.cs` | **new** — 13 tests |
| `tests/.../DashboardRegistryReadTests.cs` | ⚠️ **outside my ownership** — see below |

**Ownership deviation:** `DashboardRegistryReadTests.cs:1349` asserted `hx-swap="outerHTML"`, which the approved
morph change necessarily invalidates. I updated it to assert `hx-ext="morph"` + `hx-swap="morph:outerHTML"` —
the minimal edit to track the new approved contract. Not a weakening (it asserts strictly more than before).
Leaving it red was the only alternative. Flagging for the lead since the file is not in my list.

---

## Miller calls used (and what each confirmed)

| Call | Confirmed |
|---|---|
| `workspace(operation=list, filter=miller)` | Worktree index sidecar was missing; routed orientation to `miller-b275269b2d7c` (main checkout, rev 579 = branch baseline). |
| `context(query='dashboard fragment endpoints htmx polling')` | Entry points: `DashboardEndpoints`, `MapDashboardEndpoints`, `applyVisibilityPolling`, `DashboardHostPipeline`. |
| `inspect(DashboardHostPipeline.Configure, scope=…, depth=full)` | **Exact** pipeline body: explicit `MapMethods` per-file static routes, `app.Use` exception wrapper style, `UseRouting`/`UseAntiforgery` ordering → where to insert middleware and the route. |
| `inspect(src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs)` | Symbol list of the endpoints file. |
| `inspect(MapDashboardEndpoints, scope=…, depth=full)` | Fragment route signatures: `/fragments/{activity,dashboard,workspaces,telemetry}`, their component types, params, and `PreventStreamingRendering = true`. |
| `inspect(DashboardData.ReadIndex, scope=…, depth=full)` | `ReadIndex(string registryDbPath) → DashboardWorkspaceIndex`; renders no timestamps ⇒ deterministic body ⇒ ETag is content-stable (isolating the token as the *only* nondeterminism). |
| `trace(MapDashboardEndpoints, mode=refs)` | Sole caller is `DashboardHostPipeline.Configure:111` ⇒ no other pipeline to keep in step. |

## API-shape evidence (nothing inferred)

- **Fragment endpoint shapes** — `inspect(MapDashboardEndpoints, depth=full)` body.
- **`ReadIndex` / `ReadRecentActivity` / `ReadTelemetrySummary` signatures** — `inspect` + the existing call
  sites in `DashboardEndpoints.cs`.
- **`RenderComponentAsync` + `FixedAntiforgeryStateProvider`** — read verbatim from
  `DashboardActivityFeedTests.cs:741-763`.
- **TestServer host + antiforgery cookie harvest** — read verbatim from `DashboardMutationEndpointTests.cs:148-175`.
- **`DashboardPaths` ctor order** (`registry, telemetry, tools, webRoot, url`) — `DashboardMutationEndpointTests.cs:30-35`.
- **Idiomorph surface** — only the contract-input usage, re-verified against the downloaded bytes.

## Self-review findings

- Middleware placed **after** the exception wrapper so a throwing fragment still degrades to the wrapper's 500;
  `finally` restores `Response.Body` before it runs, so the 500 reaches the real socket. Buffering keeps
  `HasStarted == false`, so the 304 status rewrite is always legal.
- `If-None-Match` parsing handles comma-lists and `*`.
- 304 clears `Content-Length`/`Content-Type` per RFC 9110.
- ETag stored at module scope, not on the DOM node — a morph swap rewrites attributes and would clobber a
  node-parked ETag (the very swap it guards).
- Added `NonFragmentGet_IsNotETagged` to pin the middleware's blast radius to `/fragments/*`.

## Judgment calls

- `DashboardHostPipeline.cs:47` — chose middleware **after** the exception wrapper over before it, so fragment
  exceptions keep the existing 500 degrade path.
- `dashboard-site.js:~200` — chose the 304 guard at the **top** of `htmx:beforeSwap`, returning before
  `captureIssueDetailsState`, because on a 304 nothing swaps and capturing state would be pointless work.
- `alpine-components.js:34` — extracted `rehydrate()` rather than calling `init()` from the listener; Alpine
  owns `init()` lifecycle and re-entering it would re-read the store and clobber live user state mid-session.
- `DashboardFragmentCachingTests.cs` — kept the two criterion-encoding tests **red** rather than deleting them.
- `DashboardRegistryReadTests.cs:1349` — updated rather than left red (see *Ownership deviation*).

## Concerns

1. **Blocker above needs a lead decision** (recommend A). Acceptance criterion 1 and the plan checkboxes are
   untickable until then — I ticked nothing.
2. **ETags are content-stable but not eternal**, by design: any real content change (including a server-rendered
   humanized timestamp bucket flipping, e.g. `just now` → `5s ago` in the activity feed) legitimately mints a
   new ETag and a 200. That is correct behavior, not a defect — but it means the activity feed's 304 hit-rate
   is bounded by its own relative-time churn, so expect fewer 304s there than the 5s cadence suggests.
3. **Later tasks depend on the `data-issue-details` contract** established here; it is in place and tested.

---

# Fix round — Option A (lead ruling)

Lead approved the implementation as-is, accepted the `DashboardRegistryReadTests.cs:1349` ownership deviation,
and ruled: implement Option A — token-normalized, cookie-salted fragment hashing. Tokens themselves are neither
stripped nor stabilized; the 200 body keeps shipping real tokens.

## What changed

`DashboardHostPipeline` (now `internal static partial class`, for `[GeneratedRegex]`):

- **`ComputeFragmentETag(byte[] payload, HttpRequest request)`** — replaces the raw `SHA256.HashData(payload)`.
  Hash input = *masked payload* + `\u0000` + *antiforgery cookie salt*. The bytes written to the client on a
  200 remain the **original, unmasked `payload`**.
- **`AntiforgeryTokenValue()`** — `[GeneratedRegex]` matching `name="__RequestVerificationToken"\s+value="…"`,
  applied with `Regex.Replace` (**replace-all**, per the requirement: the workspaces fragment renders one token
  per remove-confirm form plus the prune form, so first-match-only would leave later tokens poisoning the hash).
- **`AntiforgerySalt(HttpRequest)`** — first request cookie whose name starts with `.AspNetCore.Antiforgery.`,
  concatenated as `name=value`; absent cookie → `string.Empty`.
- Strong-ETag format and `If-None-Match` semantics unchanged, as required.

**Why the salt is load-bearing (not belt-and-braces):** masking alone would let a 304 strand a token in the
live DOM that was minted for a *different* cookie — a latent 400 on the next remove/prune POST. Salting makes a
rotated cookie change the ETag, forcing a 200 with a freshly matched token. The rotation test pins exactly that.

**Graceful degradation:** if the regex ever stops matching (e.g. a future ASP.NET changes attribute order), the
ETag simply becomes unstable again → always 200 → no 304s. Lost optimization, never a correctness bug. The
stability test fails loudly if that happens.

## New tests (both required by the ruling)

- `FragmentWorkspaces_WithADifferentAntiforgeryCookie_ReturnsFresh200WithADifferentETag` — cookie-rotation pin.
  Rotation is produced the server's own way: a request carrying no antiforgery cookie mints a fresh one. The
  test asserts the two cookies genuinely differ before asserting the ETag differs, so it cannot pass vacuously.
- `FragmentWorkspaces_ServedBody_ShipsARealTokenNotTheHashingMask` — token-integrity pin. Asserts the mask
  string never appears in served bytes **and** that a real token is present (`value="CfDJ8…"`, the Data
  Protection payload prefix), so masking can never leak into the response.

## Verification ledger — fix round

| Invariant proven | Scope | Command | Commit | Result | Timestamp |
|---|---|---|---|---|---|
| Full Task-1 contract incl. both previously-red criterion tests + rotation/integrity pins | worker-red-green (focused) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "(Category!=Scale)&(FullyQualifiedName~DashboardFragmentCaching)"` | this commit | **15 passed, 0 failed** | 2026-07-16 |
| No regression across the fast suite (was 3514/2 red) | worker-red-green | `scripts/test.sh` | this commit | **3518 passed, 0 failed** | 2026-07-16 |
| 0 warnings / 0 errors (warnings are errors) | worker ceiling | `dotnet build Miller.slnx -c Release` | this commit | **Build succeeded, 0 Warning(s), 0 Error(s)** | 2026-07-16 |

The two criterion tests went green **as written** — unchanged from when they were red. No acceptance criterion
was reworded, weakened, or deleted to get here.

## Judgment calls — fix round

- `DashboardHostPipeline.cs:202` — chose `\u0000` as the content/salt separator over a space. A space is
  ambiguous in principle (content and salt are both text); NUL occurs in neither HTML nor a cookie value, so no
  two distinct inputs can collide into one hash. Also replaced a **literal NUL byte** that landed in the source
  during editing with the explicit `\u0000` escape — an invisible control character in source is a latent trap.
- `DashboardHostPipeline.cs` — chose `[GeneratedRegex]` + `partial` over a `static readonly Regex`, matching
  the repo's warnings-as-errors posture (avoids SYSLIB1045) and compiling the pattern at build time.
- Rotation test — chose the server's own mint-on-absent-cookie path over hand-forging a second cookie, so the
  test exercises real rotation rather than a synthetic value the server never issued.

## Plan checkboxes

All five Task 1 acceptance boxes ticked in `docs/plans/2026-07-16-dashboard-ux-fixes.md`. Verified the tick
targeted Task 1's `serial-worker-commit` box only — Task 5's identically-worded box remains unticked.

## Concerns — fix round

1. **None blocking.** Task 1 is complete and green.
2. Carried forward from the first round (still true, still worth knowing): ETags are content-stable but not
   eternal — the activity feed's server-rendered relative timestamps (`just now` → `5s ago`) legitimately mint
   new ETags, so its 304 hit-rate is bounded by its own time-bucket churn rather than the 5s cadence. Correct
   behavior, not a defect.
3. For later tasks touching swap semantics: htmx 2's default `responseHandling` swaps **any** 3xx, so the
   `htmx:beforeSwap` 304 guard in `dashboard-site.js` is correctness, not optimization — do not remove it.
4. Tasks 5/6/7 edit `WorkspaceIndex.razor` / `alpine-components.js` / `dashboard-site.js` on top of this. The
   contracts they inherit: `data-issue-details` + `data-issue-id` persist-open, the element-scoped
   `htmx:afterSwap` → `rehydrate()` pattern, and the module-scope (never DOM-parked) state stores.
