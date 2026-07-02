# julie-extract 2.6.1 Pin Bump Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Bump Miller's julie-extract pin from 2.6.0 to 2.6.1 (containing-symbol binding fixes built for Miller's bridge), verify the HTTP-boundary bridge stack against the corrected bindings, and retire the 2.6.0 binding-quirk workarounds baked into test comments, fixtures, and docs.

**Architecture:** No Miller provider/consumer code changes are required — 2.6.1's fix is purely upstream, and the four-agent examination (2026-07-02) confirmed Miller has no variable-skipping compensation to delete and that every NULL-symbol fallback stays load-bearing (module-level facts legitimately remain NULL under 2.6.1). The work is: mechanical pin bump → live Scale verification → stale comment/fixture/doc refresh.

**Tech Stack:** .NET 10, xUnit v3, julie-extract 2.6.1 (Rust binary, pinned download).

**Architecture Quality:** No Architecture Impact. Mechanical bump plus test/doc hygiene; edge/scoring/traversal logic untouched.

## Why 2.6.1 matters to Miller (examination summary)

Release v2.6.1 (supersedes v2.6.0; SQLite schema stays 3, integer `extract_contract_version` stays 3 — Miller's runtime gates in `MillerExtractContract` need **no** constant changes beyond the download pin):

1. Structural facts bind to scope-bearing containers; `variable`/`constant`/`enum_member`/`import` are no longer containment candidates.
2. `http.client_request.v1` inside `const res = await fetch(...)` binds the enclosing **function**, not the `res` local.
3. `nextjs.route_handler.v1` for `export const VERB = async () => ...` binds the exported handler symbol instead of NULL.
4. Same-line route-object facts no longer bind child object-property symbols; module-level route objects with no scope-bearing owner **stay NULL** (this is why Miller's symbol-less fallbacks must be kept).

Biggest behavioral payoff: the F4 literal-suppression key in `DotnetWebBridgeProvider.DedupeClientCalls` (`src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:382-438`) is `(containing symbol id, canonical route)`. Under 2.6.0, a `const res = await fetch(...)` site bound the structural fact to the `res` variable while the legacy URL-literal bound to the function — different ids, suppression silently missed, fabricated duplicate edges survived. Under 2.6.1 both bind the function and suppression fires. Secondary payoffs: trace-by-function-name now works for res-shaped client sites; const-arrow Next.js handlers land on real symbols; symbol-level `IsTest` exclusion becomes effective (`StructuralRouteFactAdapter.IsTestFact`).

## Global Constraints

- Pinned version string is exactly `2.6.1` everywhere it appears (pins file, contract const, two test assertions).
- Asset sha256s (computed locally 2026-07-02 from downloaded archives; byte-identical to the digests published in the v2.6.1 release notes). Enforcement: `scripts/restore-julie-extract.sh` re-verifies the committed digest against its own download and deletes the archive on mismatch — a successful restore in Task 1 IS the commit-blocking digest verification.
  - `aarch64-apple-darwin`: `f68a346397d8f016311867cbda60b14c8b8027e45aa89ba9273cbc6c0ff85421`
  - `x86_64-apple-darwin`: `e05ad64b30ef3b44bc47644913c753126cbd53c76d99d54b6cbd0bf581e9dc33`
  - `x86_64-unknown-linux-gnu`: `8e681eee03bf568c7846c2e8f9be5b89cc19f507db764bd010b9856d9a008d1a`
  - `x86_64-pc-windows-msvc`: `2be36d45730d58c3cb7f193f503abf773b15ab83397b82e9139223f17157832d`
- Do NOT touch `MillerExtractContract` schema/contract constants (lines 16–20): 2.6.1 keeps schema 3 / contract 3 / report 3 / blake3.
- Do NOT edit `.github/workflows/*`: the release matrix keys on triples (unchanged) and workflows read `julie-pins.json` at run time. Keeping workflow files untouched also keeps the bump commit safe w.r.t. the tag-push 403 rule (CLAUDE.md).
- No Miller release: no `Directory.Build.props` version change, no plugin-manifest change, no README release metadata.
- Bump-first ordering (d17f300 precedent): land the pin before test/doc refresh so all verification runs against the new extractor.
- Do not weaken live-test guards to make them pass: the HonestyProbe precision floor (≥ 0.75) and Guard assertions stay; expectations may only move toward 2.6.1-true behavior.
- Goldfish checkpoint BEFORE each commit; `.memories/` is git-tracked and ships in the commit.
- No push without explicit user approval.

## Verification Strategy

**Project source of truth:** CLAUDE.md (Testing section), `scripts/test.sh`.

**Worker red/green scope:** `scripts/test.sh` (fast suite, `Category!=Scale`, <30s tripwire). Baseline 2633 green at HEAD `d905857`.

**Worker ceiling:** `scripts/test.sh scale` (spawns the restored `.tools/julie-extract`). Baseline 38 green. Scale is load-bearing for this bump — it is the only place real 2.6.1 bindings are observed.

**Worker gate invariant:** Fast suite proves the version-pin plumbing (contract const, `capabilities --json`) and that no pure-logic behavior drifted; Scale proves the bridge stack against live 2.6.1 extraction output.

**Lead affected-change scope:** `dotnet build Miller.slnx -c Release` (0 warnings / 0 errors; `VerifyPinnedJulieExtractVersion` guard passes only with a restored 2.6.1 binary) plus both suites after each task's commit.

**Branch gate:** fast + scale + Release build all green at the final commit.

**Escalation triggers:** Any Scale test red after the bump → do NOT immediately edit the test; first extract the fixture live and inspect actual `containing_symbol_id` values to decide whether the assertion encoded a 2.6.0 quirk (fix expectation) or a real 2.6.1 regression (report upstream, stop). A flip of the Nuxt whole-file NULL behavior (see Task 2) invalidates doc claims in `trace-json-v1.md:146-148` and must be reconciled before Task 4.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate (Task 3 comment/fixture refresh is the only sanctioned test-file editing).

**Verification ledger:** Record command, scope, commit SHA, result, timestamp in the final report (fast/scale counts + build result per task commit).

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent; no harness routing docs) → `inherit` for all tiers.

**Strategy / Implementation / Mechanical / Gate-interpretation / Escalation tiers:** Harness mapping: `inherit` (session model). **Worker eligibility:** all tasks are bounded and evidence-backed; any worker may take them. **Mechanical exclusion:** Task 4 (docs/skill) owns no test gates; Tasks 1–3 own the suites they run.

---

### Task 1: Mechanical pin bump (bump-first)

**Files:**
- Modify: `scripts/julie-pins.json:2,7-10` (version + four sha256 values; asset `name` fields use `{VER}` templates — do not touch)
- Modify: `src/Miller.Indexing/MillerExtractContract.cs:24` (const → `"2.6.1"`, comment → `// julie-extractors release tag v2.6.1 (published 2026-07-02).`)
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs:21` (`Assert.Equal("2.6.1", ...)`)
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:285` (`pinned_version` → `"2.6.1"`)

**Interfaces:**
- Consumes: sha256 digests from Global Constraints (already verified against upstream's published digests).
- Produces: a restored `.tools/julie-extract` reporting `julie-extract 2.6.1`; all later tasks extract with this binary.

**What to build:** The exact four-file bump d17f300 performed for 2.6.0, with 2.6.1 values. Then re-run restore and build so the `VerifyPinnedJulieExtractVersion` guard (in `src/Miller.Server/Miller.Server.csproj`) flips from fail-on-stale to silent pass.

**Approach:** Edit files → `bash scripts/restore-julie-extract.sh` → `.tools/julie-extract --version` prints `julie-extract 2.6.1` → `dotnet build Miller.slnx -c Release` → `scripts/test.sh`. Goldfish checkpoint, then commit `chore: bump julie-extract pin to 2.6.1 (containing-symbol binding fixes)` citing the four sha256s.

**Acceptance criteria:**
- [ ] `scripts/julie-pins.json` has version `2.6.1` and the four Global-Constraints sha256s; restore succeeds (digest check passes).
- [ ] `.tools/julie-extract --version` → `julie-extract 2.6.1`; Release build 0W/0E.
- [ ] Fast suite green (2633; the two pin assertions flipped with the bump).
- [ ] Committed with `.memories/` checkpoint included.

### Task 2: Live Scale verification against 2.6.1 bindings

**Files:**
- Test: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (run, not edit — editing belongs to Task 3)

**Interfaces:**
- Consumes: restored 2.6.1 binary from Task 1.
- Produces: live evidence (pass/fail per test + observed binding facts) consumed by Tasks 3–4.

**What to build:** Run `scripts/test.sh scale` (38 baseline) and adjudicate the two tests that exercise the exact re-bound `const res = await ...` shape:
- `DisciplinedFixture_AllThreeLegs_KnownBridgeSet` (`LiveBridgeTraceTests.cs:47`; client display assertion at `:93-94`; fixture `const res = await axios...` at `:743,:748`). Expected: still green and more deterministic — F4 suppression now matches, the legacy-literal duplicate edge disappears, the single structural edge sources at `fetchAppSetting`.
- `HonestyProbe_UndisciplinedFixture_GuardsHold_PrecisionAndRecall` (`:511`; trap (c) `const res = await fetch("/api/reports")` at `:1200-1203`). Expected: green; precision only improves when the duplicate collapses; Guard 3 test-exclusion strengthens (symbol-level `IsTest` now effective).

Also confirm `NuxtServerFixture_AxiosAndSuffixlessRoutes_HighAndHonestMedium` (`:393`) stays green — it is the live proof that suffix-less Nitro whole-file facts **still** come back NULL-bound under 2.6.1 (the claim `docs/contracts/trace-json-v1.md:146-148` depends on).

Third adjudication point (codex review finding): F4's "same site" key is coarse — `(containing symbol id, canonical route)`, not a call-site span (`DotnetWebBridgeProvider.cs:373-380,389-405,428-437`). 2.6.1's rebinding makes it fire for res-shaped sites, which also means a *distinct* uncovered-wrapper literal (ky/got/$fetch) in the same function to the same canonical route is now suppressed alongside the fact-covered fetch. This tradeoff already existed under 2.6.0 for bare-call fetch facts (function-bound all along); 2.6.1 extends it to res-shapes rather than creating a new class. Confirm from the live runs that no fixture regresses on it, and hand the semantics-pinning test to Task 3. If a live fixture shows an unacceptable recall loss, stop and report — an F4 key refinement is provider code and out of this plan's scope.

**Approach:** If all 38 green, record counts and proceed. If red, follow the escalation trigger (extract fixture live, inspect `structural_facts.containing_symbol_id`, classify quirk-encoding vs regression). No commit from this task unless a test expectation legitimately moves to 2.6.1-true behavior — in that case fold the change into Task 3's commit.

**Acceptance criteria:**
- [ ] `scripts/test.sh scale` result recorded; all tests green or every red adjudicated with live extract evidence.
- [ ] Nuxt whole-file NULL binding behavior confirmed (or flagged as flipped, blocking Task 4's doc claims).

### Task 3: Retire 2.6.0-quirk comments and strengthen live fixtures

**Files:**
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:324-328, 908-914, 975-976` (comments state the opposite of 2.6.1 behavior)
- Modify: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:267-268, 303-304, 409` only if the comment reads as a current 2.6.0 binding quirk rather than a historical family label; the NULL fixture **row** at `:293-295` stays data-only — the loader must tolerate NULL regardless, and the shape stays realistic for whole-file routes.
- Modify: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (add the F4 coarse-key semantics-pinning test; nearest existing coverage is the different-route/different-symbol survival test at `:2648-2704`, which does NOT cover same-function same-route)
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:372-380` (XML summary must no longer claim uncovered wrapper literals "always survive" once the accepted coarse key suppresses same-function/same-route wrapper literals)

**Interfaces:**
- Consumes: Task 2's live evidence.
- Produces: live assertions covering 2.6.1 changes #2 and #3 directly.

**What to build:** Rewrite the four stale comment blocks to state 2.6.1 semantics (scope-bearing binding; const-arrow handlers symbol-bound; module-level stays NULL). Then strengthen coverage where the old workarounds hid the new behavior:
1. In `NextApiFixture_AttestedPostFetch_HitsPostRouteHandlerSymbol` (`:307`), the GET handler is a const-arrow export precisely because 2.6.0 left it NULL. Add an assertion that the const-arrow handler's fact is now symbol-bound (change #3) — e.g. assert the GET handler symbol appears among endpoint/observation nodes with a real `SymbolId`, or assert its `structural_facts` row carries a non-null `containing_symbol_id` resolving to the exported handler.
2. In `DisciplinedFixture_AllThreeLegs_KnownBridgeSet`, pin the F4 win: assert exactly one client edge per res-shaped call site (the literal is suppressed), removing the `SingleEdgeOfKind` ordering dependence noted in the examination.
3. Add a fast-suite `BridgeGraphBuilderTests` case pinning the F4 coarse-key semantics under function-bound facts (Task 2's third adjudication point): a function containing a fact-covered fetch AND a distinct uncovered-wrapper literal to the SAME canonical route → the wrapper literal is suppressed (same `(symbol, route)` key). Comment the test with the accepted tradeoff: F4 suppresses per `(containing symbol, canonical route)`, not per call-site span — 88a8ee1's design, now uniformly active under 2.6.1. Different-route/different-symbol wrapper survival stays covered by `:2648-2704`.

Comment-scope rule (codex review finding): only comments asserting the 2.6.0 binding **quirks** as current behavior are in scope. Version-history labels ("2.6.0 fact families", "julie-extract 2.6.0+ introduced…") on adapters/providers/test banners (`StructuralRouteFactAdapter.cs:82,85,126`, `FileRouteBridge.cs:42,47`, `RouteBridge.cs:32,210`, `DotnetWebBridgeProvider.cs:277`, `FileRouteBridgeProvider.cs:119`, and test banner comments) are accurate history and STAY untouched. One exception is the `DedupeClientCalls` XML summary: it describes current F4 behavior, so update it with the accepted coarse-key semantics.

Keep all still-load-bearing NULL-fixture fast tests untouched (`BridgeGraphBuilderTests.cs:2043,:2454,:2509,:2718,:2743,:2820,:2877,:2914`; `TraceToolTests.cs:1204,:1923-1924,:1958-1959` — module-level/whole-file shapes 2.6.1 explicitly keeps NULL).

**Approach:** TDD where behavior is asserted (new assertions must be shown to hold against the live 2.6.1 extract via the scale run); comment rewrites are mechanical. Run `scripts/test.sh` + `scripts/test.sh scale`. Checkpoint, commit `test: assert 2.6.1 containing-symbol bindings live; retire 2.6.0 quirk comments`.

**Acceptance criteria:**
- [ ] No comment in the repo states the res-variable binding or const-arrow-NULL emission as current behavior (version-history "2.6.0" labels stay).
- [ ] Live assertions cover change #2 (function-bound client request + F4 suppression) and change #3 (const-arrow handler symbol-bound).
- [ ] Fast-suite test pins F4 same-function same-route wrapper-literal suppression as intended coarse-key behavior.
- [ ] Fast + scale suites green, committed.

### Task 4: Docs, skill, and memory refresh

**Files:**
- Modify: `docs/contracts/trace-json-v1.md:142-148`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md:38` (then regenerate `skills/miller-bridge-trace/SKILL.md` via `scripts/sync-plugin-skills.sh`)
- Create: goldfish checkpoint superseding the stale fixture guidance in `.memories/2026-07-02/040819_c309.md:39-41` and closing the upstream-relay follow-up in `.memories/2026-07-02/052435_cd16.md` (do not edit dated checkpoints)

**Interfaces:**
- Consumes: Task 2's confirmation that Nuxt whole-file facts stay NULL-bound.
- Produces: contract docs matching live 2.6.1 behavior.

**What to build:**
1. `trace-json-v1.md:144` — replace the "bound to the exported handler symbol when the fact carries one" hedge: both `export async function VERB` and `export const VERB = async () => ...` shapes bind to the exported handler symbol (julie-extract 2.6.1+; 2.6.0 bound only function declarations). Keep the nuxt-api synthesized-endpoint sentence (`:146-148`) as-is once Task 2 confirms it.
2. `SKILL.md:38` — "audit the 2.6.0 fact families" → version-neutral ("the HTTP boundary fact families"); regen the `skills/` copy and confirm both copies match.
3. Superseding memory checkpoint: 2.6.1 restores the decided contract at `docs/plans/2026-07-01-http-boundary-bridge-consumption.md:31`; the "fixture client calls must avoid intermediate consts" guidance is obsolete; the two upstream binding notes are resolved. The historical plan doc itself stays untouched.
4. No changes: `MILLER_AGENT_INSTRUCTIONS.md`, `docs/contracts/cli-eros-v1.md`, `CLAUDE.md`/`AGENTS.md` (no sync-agents run needed), `README.md`.

**Approach:** Edit → `scripts/sync-plugin-skills.sh` → `git diff --stat` sanity → fast suite (`AgentInstructionsTests` untouched but cheap to confirm) → checkpoint → commit `docs: state 2.6.1 containing-symbol binding semantics in bridge contracts`.

**Acceptance criteria:**
- [ ] `trace-json-v1.md` states the both-shapes binding with correct version markers; no doc claims 2.6.0-only binding semantics as current.
- [ ] Source and generated skill copies match and are version-neutral.
- [ ] Superseding memory checkpoint committed.

---

## Aftermath (no action required, worth knowing)

On first run with the 2.6.1 binary, each workspace's leader detects `ArtifactOlderThanOwn` and auto-runs one forced full rescan per claim (`IndexerService`, source tag `extractor-upgrade`), extracting into `symbols.db.rebuild` and atomically promoting — every existing artifact re-emerges with corrected bindings, zero user action. A live 2.6.0 leader elsewhere is displaced via graceful `yield`; other Miller checkouts sharing a workspace must re-run restore to regain indexing eligibility (they become permanent readers against a 2.6.1-stamped artifact). Never kill processes manually.
