### Task B6: Status/health facts, telemetry, canary-contract plumbing

**Files:**
- Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, the status/health render + JSON seams, telemetry record types + writer
- Test: `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`, telemetry contract tests

**Interfaces:**
- Consumes: cursor/generation/session facts from B2–B5; vectors-v1 §Status vocabulary (full compact vocabulary + JSON-only exact revisions/coverage/fingerprints); `canary-telemetry-v1.md` field set.
- Produces: full `vectors:` status vocabulary in compact (`ready | ready (updating; N files pending) | building N% (not queryable) | unavailable (reason) | incompatible | circuit-open | disk-blocked | disabled`) reporting the laggier cursor; exact fields in `workspace status --json` / `workspace health --json` (additive per `workspace-status-v1.md`/`workspace-health-v1.md`); telemetry: semantic participation/reason fields + canary plumbing (assignment unit, query-class enum, experiment/arm id, opaque result ids, success event) recorded but with NO experiment activation — fields exist and are exercised by tests, arm assignment is a constant `control` until P5.

**Contract inputs:** canary-telemetry-v1 verbatim field names; privacy rule — persisted telemetry is enum/counter-only, proven query-free by a test.

**File ownership:** Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, render seam, telemetry records + writer, their tests

**Serialization required:** Yes

**Dependency reason:** Reports cursor/generation facts produced by B2–B5; canary fields ride the telemetry seam last to avoid churn.

**What to build:** The observability half of lane b: everything a P4 shadow rollout needs to be diagnosable, and the canary schema ready so P5 flips a switch rather than migrating telemetry.

**Acceptance criteria:**
- [ ] Every status vocabulary state renderable and covered by a test; compact shows laggier cursor; JSON carries exact revisions/fingerprints
- [ ] Canary-contract fields present per canary-telemetry-v1 with constant control arm; telemetry proven query-text-free by test
- [ ] Existing status/health JSON consumers unaffected (additive only; contract tests green)
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

