### Task D2: Fuzzy policy replay evaluation + stale-target convergence wait

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs` (plan-time stale_target bounded wait mirroring the 2.5s apply-path wait), fuzzy matcher policy constants/code (locate via the fuzzy rung of the match ladder)
- Create: `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: D1's `edit_failure_reason` instrumentation; historical edit-failure telemetry (telemetry.db export) as the replay corpus where retrievable, plus synthesized fixture failures for the policy tests.
- Produces: plan-time stale-target bounded wait + retry; a proposed fuzzy policy (snippet cap / distance ceiling) with before/after replay numbers in the findings doc; the policy change itself ONLY if replay shows strict improvement (otherwise document and keep current policy).

**Contract inputs:** Design §7 items 4–5. Current policy facts: 160-char snippet cap, distance ceiling 3, zero historical fuzzy successes.

**File ownership:** Modify: `src/Miller.Server/Tools/EditTool.cs`, fuzzy matcher policy code; Create: `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`; Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** Yes (after D1)

**Dependency reason:** Needs D1's failure-reason instrumentation to build the replay corpus.

**What to build:** The judgment half of the edit lane: measure, then change policy only on evidence.

**Acceptance criteria:**
- [ ] Plan-time stale_target waits (bounded) and succeeds when index converges within budget; still fails cleanly after
- [ ] Findings doc reports replay methodology + numbers; any policy change is gated on those numbers
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

