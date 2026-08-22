# CT cross-repo dogfood findings (2026-08-21)

First real-repo continuous-testing runs outside miller itself, run as a release gate for the
v1.21.0 CT announcement (user-directed: verify the provider matrix on real repos before
announcing it). Repos: `rasd-vue-library-2` (vitest 0.29.8, 48 spec files),
`more-itertools` (pytest, 736 tests, cloned from GitHub), `julie-extractors` (cargo
workspace, 4,173 cases), `razorback` (`node --test`), `classnames` (cloned, `node --test`),
`vercel/ms` (cloned, jest — not yet run).

## Verdicts

| Provider | Repo | Result |
|---|---|---|
| pytest | more-itertools | **GREEN** — 736 tests in 2 file-level cases, real `.venv`, zero failures |
| vitest | rasd-vue-library-2 | **red (provider bug F2)** — every file fails at vitest's CLI parser |
| cargo | julie-extractors | **partial (provider bug F6)** — run "passed", zero results committed |
| node-test | razorback | **blocked (F1)** — discovery enables 0 projects |
| jest | ms | not yet run (needs `pnpm install`) |

## Findings

**F1 — node-test is unreachable through discovery.**
`ContinuousTestProjectInventory.TryIdentify` identifies `package.json` only by
`vitest`/`jest` tokens, so a `node --test` repo enables 0 projects even though the factory
registers `node-test` and the provider builds a `--test` run. Fix approved; in flight.

**F2 — vitest 0.x fails on `--cache.dir`.**
`JavaScriptTestProvider.IsolationArguments` always passes `--cache.dir`; dot-notation CLI
options arrived in vitest 1.x. On vitest 0.29.8 every file fails with
``CACError: Unknown option `--cache` `` — reproduced exactly with the provider's argument
shape, while a plain `vitest run` passes. Fix (version-gate the flag) in flight.

**F3 — one Python repo becomes three projects.**
more-itertools enabled `pyproject.toml`, `setup.cfg`, AND `tox.ini` as separate pytest
projects — the suite would run three times. Needs config-root dedupe (one project per
directory, priority order). Not yet fixed.

**F4 — fixture files become phantom projects.**
julie-extractors enabled 7 projects including `fixtures/extraction/toml/cargo_deps/Cargo.toml`
and a fixture `pyproject.toml` — test DATA swept up as projects, plus every workspace member
crate beside the workspace root (double execution). Needs fixture/member awareness. Not yet
fixed.

**F5 — `tests disable` output is mislabeled.**
The result lists the REMAINING enabled projects under a "disable N project(s)" heading.
Minor, confusing. Not yet fixed.

**F6 — a whole-suite Rust run executes nothing and reports "passed" (most serious).**
Diagnosed with filesystem-level proof (read-only Opus investigation, 2026-08-22):

- `ContinuousTestDaemonQueue.cs:405` sets `WholeSuite` when a run covers every known case;
  `ContinuousTestCoordinator.cs:213` then hands the provider an EMPTY id list
  (commit `9f5066aa`: "every provider treats an empty selection as run the whole assembly").
- The Rust provider's run loop is driven entirely by that list
  (`RustTestProvider.cs:253-295`): empty list ⇒ **no cargo process starts**, and
  `RunStatus([])` returns `"passed"` (`:701-704`). `MarkUnreportedRunCasesStale` flips all
  4,173 cases back to stale — verdict `partial`, forever.
- Proof: the generation's `TestResults/` directory exists and is EMPTY (every run-path cargo
  call appends an invocation log there); the 3.5 minutes was inventory rediscovery
  (`cargo metadata` + `test --no-run --workspace` + per-target `--list`), which runs outside
  the logged run path.
- Parsing and name-joining are NOT at fault: the provider's exact `RunCommand` shape against
  the real repo attributes 15/15 names, joining exactly to the stored case ids.
- Why tests missed it: no test hands a real provider an empty selection; the whole-suite
  daemon test uses a fake that reports results regardless of selection. Other providers
  survive because they treat empty-selection as "run everything" and attribute from a result
  artifact; Rust has no artifact — the id list IS the plan.
- Fix shape (proposed, approved-for-implementation pending): an explicit `WholeSuite` flag on
  `ContinuousTestProviderRunRequest` instead of a blanked id list; Rust runs per-target
  groups unfiltered under it; a provider given no ids and no command THROWS; the coordinator
  refuses to record `passed` for zero results over a non-empty selection.

**F7 — every explicit run rediscovers and rebuilds a fresh generation.**
`ContinuousTestDaemonQueue.cs:226` sets `RefreshInventory` on workspace-scope changes, so an
explicit run on julie-extractors rebuilds ~8 GB from scratch (3.5 min) before any test could
run. Separate fix; not yet scoped.

## Fix validation (2026-08-22, Debug build `4cd268b8`, daemon/one-shot paths)

All three blocking fixes were validated end to end on the real repos that exposed them:

- **F1 (node-test discovery):** razorback now enables 1 project (was 0); classnames enables 1.
  razorback's run verdict is red and HONEST — its own `npm test` fails at baseline with a real
  assertion error.
- **F2 (vitest 0.x):** rasd-vue-library-2 went from 47/47 failed-at-the-parser to 46 green and
  exactly 1 red — `RDataGrid.spec.ts`, which fails identically under a plain local
  `vitest run` (a genuine repo failure, correctly attributed with the real assertion message).
- **F6 (Rust whole-suite):** julie-extractors executed all 4,173 cases (stale 4,173 → 0,
  results committed) with 4,172 green and one red: the pre-existing
  `maintenance_heartbeat_fails_closed_after_lease_takeover` wall-clock flake on a busy
  machine, reported with the real panic text. Before the fix the same run committed ZERO
  results.

Two new findings from validation:

**F8 — node-test case discovery needs `.test.`/`.spec.` file naming.**
classnames' suite lives in `tests/*.js` (63 passing tests at baseline), and CT discovers 0
cases — verdict honestly `unknown`, but a real node-test repo with that layout gets no
coverage. Needs either directory-aware case discovery or a documented naming requirement.

**F9 — per-file failure attribution on a partially-red node-test suite is unverified.**
razorback's run marked all 34 files failed with the npm banner as every summary; the baseline
shows a genuinely red suite, but whether individually-green files are over-marked red needs a
partially-red discriminator (classnames cannot serve — see F8).

## What the fail-safes got right

CT never lied green. F2 surfaced as red with failure rows; F6 surfaced as `partial` with
everything stale — the "green requires complete results at the selected key" rule is exactly
what caught a run that executed nothing.
