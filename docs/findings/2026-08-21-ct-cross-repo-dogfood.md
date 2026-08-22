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

**F10 — a chained package test script breaks the jest run (false red).**
`vercel/ms` (jest, script `pnpm run test:nodejs && pnpm run test:edge`) passes both halves at
baseline (167 tests each) but CT marks all 4 files red with the `test:edge` npm banner as the
summary. The provider routes a jest project through its package script and appends
reporter/isolation args; a compound `a && b` script does not deliver those args to the jest
invocation that needs them. jest therefore stays "supported, not field-proven" in the
announcement (the docs already carry that constraint). Environment note: the first attempt
failed on missing `pnpm` and surfaced only as `partial` with the reason in the daemon log —
a visible failure reason on the run result would have saved a diagnosis step.

## Fix batch 2 (2026-08-22, merged at `05e5e524`)

Five findings were fixed in parallel worktree lanes and merged; unit-proven, not yet re-proven on
the dogfood repos:

- **F3 FIXED:** discovery keeps one pytest project per directory, choosing the config file pytest
  itself reads first (`pytest.ini` > `pyproject.toml` > `tox.ini` > `setup.cfg` > `setup.py`).
- **F4 FIXED:** a `Cargo.toml` proven to be a `[workspace]` member is dropped (the root run covers
  it; every parse doubt keeps the candidate), and the walk prunes `fixtures`/`__fixtures__`/
  `testdata` directories. The real julie-extractors layout goes from 6 discovered manifests to 1.
- **F5 FIXED:** `tests disable` heads the projects it turned OFF and names the remainder on a
  `remaining enabled:` line; JSON adds `changed_count`/`changed_projects` without changing any
  existing field's meaning (`docs/contracts/tests-cli-v1.md` updated).
- **F8 FIXED:** node-test case discovery follows Node's own documented default patterns (copied
  verbatim from nodejs.org; note `test/` singular — `tests/` is NOT in Node's default set), and a
  script that names positional paths/globs REPLACES the defaults exactly as on node's command
  line — which is what covers classnames' `node --test ./tests/*.js`.
- **F10 FIXED:** the provider refuses to append reporter args to a script that cannot deliver
  them — a chained script, a `run <name>` fragment of a chained `test` entry point (the actual
  vercel/ms shape: script matching used to pick the `test:edge` half alone), or a node-test script
  that already names a positional path (Node stops reading options there; measured, not assumed).
  Refused scripts run the local runner binary directly; a missing binary or a spawn failure
  (e.g. missing pnpm) fails the run with a visible reason instead of a false red.

## Fix batch 3 and field proofs (2026-08-22)

**F10 FIELD-PROVEN — jest upgrades from "supported, not field-proven".** vercel/ms re-run under
the merged fix: baseline green (`test:nodejs` 167 + `test:edge` 167), CT verdict green twice over
fresh `ct.db`s — 4/4 suites, 167/167 cases, `failure_summary` NULL on every row. Process capture
shows the local `node_modules/.bin/jest` binary invoked directly with the cache/reporter args and
ZERO pnpm/npm processes; the jest JSON report was written and parsed. One honest coverage limit,
documented rather than fixed: CT invokes jest once under the default environment, so it covers the
167-test suite once, not the 334 executions the repo's chained script gets by running the suite
under both the node and `@edge-runtime/jest-environment` environments.

**F9 CONFIRMED, then FIXED.** A purpose-built discriminator repo (4 node-test files under `test/`:
3 green, 1 red) came back ALL FOUR red, each carrying the one real assertion message — the npm
banner was gone (F10's fix worked), but `ParseNodeJunit` collapsed the whole report to one status
plus the first failure text and stamped that pair on every per-file case id, and
`JunitTestResultParser` never read the `file` attribute Node writes on each `<testcase>` (the only
per-file signal; `classname` is just the directory). Fixed test-first (merge `edef1af8`): the
parser captures `file`, the provider groups per file the way the jest/vitest JSON path always has,
a selected file the report never names goes honestly stale via the unreported-case path, and a
report with no `file` attributes anywhere (older reporters) keeps the old aggregate behavior
exactly. New multi-file partially-red fast + Scale fixtures pin it.

**F7 DESIGNED, then FIXED.** Design pass
([`docs/plans/2026-08-22-ct-f7-per-run-rebuild-design.md`](../plans/2026-08-22-ct-f7-per-run-rebuild-design.md))
found F7 is two defects: F7a, forced rediscovery on every explicit run (small), and F7b, the
dominant cost — the generation directory IS the cargo cache (`CARGO_TARGET_DIR`), so every
operation compiles the crate graph into an empty directory and the reap deletes the warm result.
Option A shipped (merge `0b08903d`): the compiler caches move to a project-stable
`<BuildOutputRoot>/cache/<tool>` beside the generations (the split `DotnetTestProvider` always had
via `--artifacts-path`); results, reports, coverage claims, and temp stay per-generation. Coverage
acceptance from the shared cache is scoped by write time against a filesystem-stamped run epoch;
the cache counts against the disk budget; the reap never touches it; two consecutive provider
failures wipe it and retry once (the recovery the per-generation directory used to provide).
Instrumented builds get their own `cache/cargo-coverage` (different `RUSTFLAGS` re-fingerprint the
whole graph). Deliberately NOT built: a coarse toolchain/lockfile fingerprint guard — cargo's own
fingerprints invalidate correctly and incrementally, and the two-failure wipe covers a poisoned
cache. F7a (skipping rediscovery) stays a follow-up; the design doc records why it must not ship
first: the explicit run is currently the only inventory-refresh path.

Small findings from the validation lanes (recorded, not yet fixed):

- **F11 — `tests run --wait` does not wait for the execution budget.** With the user-global budget
  held by another workspace it returns immediately (`verdict: unknown`, `reason: "execution budget
  held"`, `waited: false`, `paused: true`). Both validation lanes had to poll by hand; `--wait`
  waits for a verdict, not for the budget.
- **F12 — budget-pause temp directories are never reaped.** ~116 orphan `miller-ct-budget-pause-*`
  directories in `%TEMP%` dated 2026-08-19, created on budget waits and never cleaned.
- **F13 — CLI paper cuts.** `workspace open <path>` rejects the positional form (`--path` required);
  `workspace remove` leaves an untracked `.julieignore` behind in the removed root.

Still open: F7a (skip rediscovery — follow-up gated on a new inventory-refresh trigger, see the
design doc), F11, F12, F13. Known follow-up from the F10 lane: a daemon auto-run spawn failure
still reaches ct.db only as a daemon log line; a run-level reason surface needs a schema column +
contract change.

## What the fail-safes got right

CT never lied green. F2 surfaced as red with failure rows; F6 surfaced as `partial` with
everything stale — the "green requires complete results at the selected key" rule is exactly
what caught a run that executed nothing.
