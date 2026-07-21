### Task 8: Docs, runbook, closeout

**Files:**
- Create: `docs/findings/2026-07-21-p5-canary-runbook.md`
- Modify: `docs/README.md` (map pointer)
- Modify: `README.md` (environment/configuration section — locate the existing env table with Miller search before editing)
- Test: none (docs); `AgentInstructionsTests` must stay green untouched (no guidance-channel changes)

**Interfaces:**
- Consumes: everything shipped in Tasks 1–7.
- Produces: the operator runbook: enabling the canary (`MILLER_SEMANTIC_CANARY=on` + `MILLER_SEMANTIC=shadow|on`), what gets recorded and where, running `miller telemetry canary --json` / `--gate`, reading underpowered/indeterminate verdicts, the 30-day retention squeeze (export before rows age out), and the model-swap how-to (`MILLER_SEMANTIC_MODEL`, the shadow-rebuild it triggers, rollback via retained generations) with the current registry (qwen3-0.6b-f16 default, bge-small-en-v1.5-f32) and a placeholder note that the model comparison list/eval is a later phase.

**Contract inputs:** Documented env vars and CLI flags must match the shipped spellings exactly. README release facts stay untouched (no release in this plan).

**File ownership:** `docs/findings/2026-07-21-p5-canary-runbook.md`, `docs/README.md`, `README.md` (env/config section)

**Serialization required:** Yes

**Dependency reason:** Documents behavior shipped by Tasks 1–7.

**What to build:** The operating documentation that makes the canary runnable by the user (and future sessions) without re-reading the frozen contract.

**Acceptance criteria:**
- [ ] Runbook covers enable → observe → export → gate → interpret, plus model swap; docs/README map updated.
- [ ] Every documented command/env var spelling verified against the shipped code.
- [ ] Worker-scope verification passes (fast suite green) and the change is committed per commit mode.
