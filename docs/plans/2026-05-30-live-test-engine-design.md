# Live-Test Engine — agent-friendly continuous testing

**Status:** PARKED design (parking-lot). Not scheduled. M4 (cross-language resolver) is the real
next work and this is intentionally behind it. Captured 2026-05-30 from a brainstorming session so
the design survives compaction.

**One-line:** a Wallaby/NCrunch-style continuous-testing engine *for coding agents* — so an agent can
ask "did my edits break anything I have a test for, and what did I just leave unverified?" instead of
compulsively re-running the whole suite after every trivial change.

---

## 1. Problem & intent

**Observed behavior:** agents run `dotnet test` / `npm test` obsessively — after every one-line edit,
rename, or comment change. It is slow, token-expensive, and unnecessary for the ~80% of edits that
don't touch tested behavior.

**Goal:** give agents a *cheap, trustworthy* read of test state so they skip the compulsive re-run.

**Critical distinction — the goal is behavior change, not tool existence.** A `get_test_status` tool
can exist and agents will *still* run tests, because:

1. **Verification prior** — models are trained to confirm by executing; a bare external "green" reads
   as an unverified claim.
2. **Explicit instructions** — TDD skills + CLAUDE.md say "run the tests." A passive tool loses to an
   active instruction.
3. **Staleness uncertainty** — if freshness is unknowable, re-running is always the safe default.
4. **No felt cost** — the agent doesn't *experience* a 30s run as expensive the way a human does, so
   "save time" alone doesn't motivate it.

The engine is necessary but **not sufficient**. What changes behavior is the *output framing* + a
*behavioral skill stanza* (see §6).

**Honest success bar:** eliminate the *compulsive* re-run after trivial edits (the 80% case). Running
at genuine decision points (about to commit, feature done) is **correct behavior, not failure.** Do
not oversell this as "agents never run tests again."

---

## 2. The reframe: a safety tool, not a status tool

Wallaby answers "what's the state of my tests?" (status). The agent's real question after an edit is:

> **"Did I just break anything I have a test for?"**

That's a **safety question**, and the common answer is **no**. So the headline output is a *negative
claim*: "nothing you changed is covered by a now-failing test." Why this is better:

- **It's the common case** → the tool's most frequent answer is a confident, cheap "you're clear."
- **Cheaper to make trustworthy** → a green *status* needs the whole relevant suite re-run; a
  "you didn't break anything" claim only needs *the tests covering your changed lines* re-run and
  passing. Narrower scope = faster, fresher, easier to defend.
- **It's exactly what the agent acts on** → "you're clear" → keep working.

### The trap: a negative claim is only as good as "covered by a test"

> `"Nothing you changed is covered by a failing test"` can be true because **the code has no test at
> all.** That is a **false all-clear — the most dangerous output the tool can produce.**

**Mitigation — the dual claim.** The negative claim MUST carry its denominator. The **uncovered set
is co-headline, not a footnote**:

> ✅ "Your edits to A.cs, B.cs are covered by 9 tests; all 9 re-ran 0.8s ago, 0 failures.
>    C.cs has NO covering test — 14 lines unverified."

"You're clear *where you have coverage*, and here's exactly where you don't." This is both more honest
than a green and more useful — it tells the agent where to *add* a test, which is the TDD-aligned next
move.

---

## 3. The loop

```
file watcher → debounce → diff changed files → (M4 impact pre-filter) → select affected tests via
coverage map → run affected in background → confirm reds → update state → agent reads check_changes (cheap)
```

The warm, long-lived Miller MCP server is what makes this pay off: the daemon outlives individual
agent calls, so the coverage map and last results are already warm when an agent connects.

---

## 4. Architecture

Mirrors Miller's existing Core/Server discipline.

- **`Miller.LiveTest.Core`** — pure, ZERO-I/O, unit-tested in ms:
  - selection algorithm (changed files → affected test units)
  - coverage-map model (granularity-agnostic: keyed by file *or* method id)
  - the **state machine** + staleness rules (pure functions over events)
  - debounce policy as pure functions over an event stream
- **I/O layer** (in `Miller.Server` or a `Miller.LiveTest` I/O project):
  - file watcher (reuse M3's watcher infra where possible)
  - `ITestRunner` adapters (process spawning, the non-pure part)
  - coverage parsing (per-runner formats)
  - `ct.db` persistence
- **Exposed via the existing Miller MCP server** — the agent uses the connection it already has.

**Identity note:** this expands Miller from *read-only structural intelligence* into a *process
executor with runtime state*. Contained to Server + the new lib. `Miller.Core` and julie-consumption
stay strictly read-only. This is a real identity expansion and should be a conscious decision when
the work is actually scheduled.

### Persistence — `ct.db`

- **Dedicated DB, NOT julie's read-only `symbols.db`.**
- Coverage map + last results keyed by **content hashes** (test file hash + source file hash) so a
  restart does not rebuild the expensive map; invalidate an entry only when its hash changes.
- Persisting last results means a fresh agent session gets instant status.

---

## 5. The trust contract (the whole point)

State per unit (file now, method later):

`green | red | running | stale | uncovered | unknown`

- **Invariant:** never report `green`/clear for a unit whose covered content changed without a
  completed passing re-run. Degrade to `stale` and say why. **The tool never lies — that's what lets
  an agent skip running.**
- `uncovered` is a **feature**, not an error: "this source has no covering test" is gold for an agent.
- **Output is an argument, not a verdict.** Every response carries its evidence: what ran, when,
  against which content hash, and an explicit discharge ("you do not need to run these"). A bare
  `{status:"green"}` does not change agent behavior; a defensible claim does.

### Flake handling — trust-critical, not a nicety

A negative claim ("you didn't break it") destroyed by a *flaky* red is a false alarm that **retrains
the agent to distrust the tool** — fatal to the whole premise. Likewise a flake-driven false green.

- **Confirm-before-report:** a red in the affected set is re-run (≥1×) before it is reported as
  "you broke this." Track confirmed-red vs flaky-red as distinct.
- Flag order-dependence / inconsistency; consider quarantine for chronically flaky tests.
- (Detailed flake policy not yet fully specced — drill before building.)

---

## 6. Adoption — the part that isn't in the tool

Tool quality alone loses to an explicit "run the tests" instruction. The deliverable is three things,
not one:

1. **The engine** (§3–§5).
2. **Output-as-argument** — the persuasion layer (§5). The output schema is a *product surface*.
3. **A behavioral skill stanza / CLAUDE.md instruction** — a first-class shipped artifact:
   > "Before running tests, call `check_changes`. If it reports the affected tests fresh-clear with
   > evidence, trust it and skip the run. Run only when status is stale/red/unknown for your change."

This is the **same adoption pattern Miller already uses** for "use julie not grep" — proven in-house.
Dropping #3 makes #1 and #2 underperform.

---

## 7. `ITestRunner` plugin seam

- `Discover` → leans on **julie's `is_test`** for cross-language discovery (language-agnostic);
  the runner maps to framework-specific test IDs.
- `Run(selection, wantCoverage)` → results (pass/fail/duration/message/stack) **+ coverage map**.
- **Granularity-agnostic coverage unit** (keyed by file *or* method id) so per-file → per-method does
  not touch consumers.
- Capability flags: `SupportsPerMethodCoverage`, `SupportsWarmHost`.

**Honest cross-language note:** *discovery* stays language-agnostic (julie). *Execution* is
necessarily per-runner — there is no universal test protocol (xunit ≠ nunit ≠ jest ≠ vitest). This is
the legitimate exception to the project's "don't hardcode a languages-I-care-about list" rule:
discovery is universal, execution adapters are inherently per-framework.

---

## 8. Coverage acquisition

The map (test → lines it executes) is a **runtime** artifact. **Amortization insight that makes this
tractable:** you pay the expensive instrumented pass *once per test-body change*; every edit after
that is a surgical re-run of the few affected tests. "Slow to build the map" is fine — it happens
rarely, in the background.

### .NET (the #1 technical risk — be honest)

- Stock **coverlet** gives *aggregate-per-run*, not per-test.
- xunit v3 (already used by `Miller.Tests`) runs on **Microsoft.Testing.Platform (MTP)** — a native
  host you can drive programmatically. This helps; you're not fighting legacy VSTest.
- **MVP (per-file):** run the MTP test project filtered to one test file at a time with MS Code
  Coverage; attribute covered source → that test file. Slow but amortized + incremental + background
  (only rebuild changed files' entries).
- **Parity (per-method):** a custom MTP **data collector** that snapshots coverage deltas at
  `TestCaseStart`/`TestCaseEnd` → per-method attribution in **one** instrumented pass + a **warm
  host** for instant re-runs. This is the real engineering and the actual road to NCrunch feel.

### JS/TS

- vitest / c8 / v8 give per-test coverage **much** more cheaply than .NET. Phase 2 forces
  `ITestRunner` honest (two real impls stop framework assumptions leaking) at low cost.

---

## 9. MCP surface

- **`check_changes`** (PRIMARY) — "since your last edits: what **broke** (confirmed-red) + what's now
  **unverified** (uncovered)." The dual claim of §2. Token-thrifty default; evidence included.
- **`get_test_status`** (secondary) — whole-suite view (counts + reds detail + scope filter +
  verbosity).
- **`run_tests`** (secondary) — force a run at a scope (all/project/file/test) for explicit
  re-validation.

---

## 10. Strategic coupling to M4

`check_changes` needs "what changed → what does it reach" = **impact analysis**, which is exactly
M4 (the cross-language resolver, Miller's differentiator). The live-test engine becomes a
**consumer of M4** — the static impact graph is the cheap pre-filter that narrows the change delta
*before* consulting the (expensive) coverage map. This is strategically tidy: it makes M4 *more*
valuable rather than competing with it. It also means this work should land **after** M4, not before.

---

## 11. Risks

1. **.NET per-test attribution in one warm run** — the #1 risk. **Spike before locking the parity
   architecture** (see §13). Everything downstream depends on whether MTP + a coverage collector can
   give per-test attribution without N processes.
2. **False all-clear from missing coverage** — mitigated by making `uncovered` co-headline (§2).
3. **Flaky red/green retraining distrust** — mitigated by confirm-before-report (§5).
4. **Map-build perf on big suites** — mitigated by incremental/background build, but **measure**.
5. **Identity expansion** (read-only → executor) — contained to Server + new lib; conscious decision
   at scheduling time.
6. **Per-file MVP under-delivers on the headline.** "Uncovered" precision (which *lines* have no
   test) concentrates the safety value, and that wants per-method/per-line. The reframe **raises the
   value of the parity path** from "nice polish" to "where the headline gets its precision." Know
   that the MVP is a stepping stone, not the product.

---

## 12. Roadmap

- **MVP:** .NET runner, per-file coverage, watch loop, state machine, `ct.db`, `check_changes` +
  the two secondary tools, the skill stanza. **Dogfood on Miller's own suite.**
- **Phase 2:** JS/TS runner (vitest/c8) — cheap coverage, forces `ITestRunner` honest.
- **Phase 3 (parity):** per-method collector + warm host. **Gated on the §13 spike.**

---

## 13. The spike to run first (when this is picked up)

**Question:** Can MTP + a coverage extension/data collector produce **per-test (ideally per-method)
coverage attribution in a single warm-host run**, without spawning one process per test?

- **If yes:** true NCrunch/Wallaby parity is far closer than the N-runs approach implies; design the
  parity architecture around the collector from the start.
- **If no:** per-file MVP via individual filtered runs is the realistic near-term ceiling; parity
  needs heavier custom instrumentation.

Run the spike against `Miller.Tests` (a suite we know cold) before committing to the parity
architecture. This is real work, not brainstorm — scope it when scheduling.

---

## 14. Out of scope (Wallaby/NCrunch features with no agent analog)

Do **not** chase these — they are human-IDE features that don't help a tool-calling agent:

- live inline variable values
- time-travel debugging
- editor gutter overlays

"Parity" here means the **coverage-driven selection engine + warm execution + the safety claim**, not
the IDE overlay. That's the tractable, agent-relevant half.

---

## Acceptance criteria (for the eventual MVP, not now)

- [ ] `Miller.LiveTest.Core` is pure (zero I/O deps), selection + state machine unit-tested in ms.
- [ ] File watcher → debounce → select → background run → state update loop works on Miller's suite.
- [ ] `check_changes` returns the dual claim (broke + uncovered) with evidence (what/when/hash).
- [ ] Trust invariant holds: no clear/green for a changed-but-not-rerun unit (degrades to `stale`).
- [ ] Reds are confirm-before-report (flake guard); confirmed-red vs flaky-red distinguished.
- [ ] `uncovered` set reported as co-headline.
- [ ] `ct.db` is separate from `symbols.db`; coverage map keyed by content hash; survives restart.
- [ ] `ITestRunner` coverage unit is granularity-agnostic (file→method without consumer changes).
- [ ] Behavioral skill stanza shipped alongside the tool.
- [ ] Dogfooded: Miller's own dev loop uses `check_changes` instead of compulsive `dotnet test`.
