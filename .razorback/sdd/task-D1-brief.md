### Task D1: Edit failure instrumentation, guidance, Unicode whitespace

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs`, the edit telemetry record type (locate via `Edit_ReplaceText_NoMatch_StampsNoMatchBucket`, tests/Miller.Tests/Server/EditToolTests.cs:1683), normalized-matching code (locate via EditTool's match ladder)
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: existing `match_mode=auto` ladder (exact→normalized→fuzzy), existing failure-bucket stamping.
- Produces: `edit_failure_reason` stamped on EVERY failure path (design §7.1 — audit all paths, not just no-match); Miller version stamped on edit telemetry records; error messages that carry the recovery action at the point of failure (scope disambiguation, mode suggestion); `Normalized` matching treating Unicode spaces (U+00A0 NBSP, U+2000–U+200A, U+202F, U+205F, U+3000) and form feed as whitespace.

**Contract inputs:** Design §7 items 1–3. Telemetry stays enum/counter-only — no query text. Existing partial instrumentation from `docs/plans/2026-07-12-telemetry-diagnosis-hardening.md` Task 1: audit which failure paths still stamp nothing before adding.

**File ownership:** Modify: `src/Miller.Server/Tools/EditTool.cs`, edit-related telemetry records, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` edit description only if within budget; Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Close design §7 items 1–3: complete failure-reason coverage, version-stamped telemetry, recovery-action error messages, Unicode-aware normalized whitespace. Description/guidance edits must respect the ADR-0001 budgets (edit description ≤900 chars; run AgentInstructionsTests).

**Acceptance criteria:**
- [ ] A test enumerates every replace_text failure path and asserts a non-empty `edit_failure_reason` on each
- [ ] Edit telemetry records carry Miller version
- [ ] NBSP/Unicode-space/form-feed variants match under `normalized` (tests per §7.3 list)
- [ ] Failure messages name the concrete next action; AgentInstructionsTests green
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

