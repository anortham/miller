# Task 1 report: freeze the broker lifecycle and transport contract

## Status

Complete on `codex/shared-semantic-broker-plan`.

- Commit: `fff0f306de28ce353dd2dd19fe2e529ba17b1ebd`
- Commit message: `docs: freeze shared semantic broker contract`
- No push or release action was performed.
- Lead-owned `.razorback/sdd/progress.md` and `.razorback/sdd/task-1-brief.md` were not edited by
  this worker and were not staged or committed.

## Implemented contract

`docs/contracts/semantic-broker-v1.md` now freezes:

- Pure compute over the separately frozen `julie.embedding.sidecar` v1 methods; no
  workspace/index/database/watcher/HTTP/PID/state/token/self-update control plane.
- Exact identity input
  `julie.semantic.broker|1|julie.embedding.sidecar|1|<model_id>|<model_sha256>`, SHA-256
  truncation, and binary-version exclusion.
- The approved flat `<miller-home>/semantic/` layout:
  `broker-<identity>.lock`, `broker-<identity>.sock`, `accelerator-v1.lock`,
  `miller-semantic-<identity>`, and `\\.\pipe\miller-semantic-<identity>`.
- Unix `0700` directory / `0600` socket permissions and service-lock-holder-only stale unlink.
- Windows full server versus short .NET client names, current-user ACL,
  `PIPE_REJECT_REMOTE_CLIENTS`, overlapped cancellable I/O, and visible degraded ownership if Job
  Object attachment fails.
- Owner stdin watcher before model load, authoritative stdin EOF, Windows
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and distinct owner/non-owner disposal.
- Spawn-loser polling through the 120-second initialization budget, capacity 64, 8:1
  interactive-to-waiting-batch fairness, and the 60-second active-request watchdog.
- One user-global accelerator lock, direct CPU startup for non-holders, and one CPU retry only
  after typed `ResourceExhausted` (`ContextAlloc` initially); ordinary `Decode`, `Encode`,
  protocol, and application failures do not demote.
- Broker-mode connection-scoped `shutdown` while stdio `shutdown` retains process-loop behavior.
- `MILLER_SEMANTIC=off` zero work, lexical fail-open without a hidden per-process model fallback,
  no new MCP tool, and approval-gated releases.

The ADR, 2026-07-19 program design, and 2026-07-21 production-readiness design explicitly
supersede process-local embedding ownership with this broker while preserving Miller's vector
artifact ownership. `docs/README.md` lists the new contract as current.

## TDD evidence

### RED

Command:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticBrokerContractTests
```

Observed before the contract file existed:

- Exit code: `1`
- Failed: `3`
- Passed: `0`
- All three failures were `FileNotFoundException` for
  `docs/contracts/semantic-broker-v1.md`.
- The failure was the intended missing-production-contract failure, not a compile error or test
  typo.

### GREEN

The same command after the contract and supersession docs:

- Exit code: `0`
- Failed: `0`
- Passed: `3`
- Skipped: `0`

This proves the contract contains the exact lifecycle, identity, transport, security, scheduling,
OOM, activation, and approval literals guarded by `SemanticBrokerContractTests`.

### Worker ceiling

Command:

```text
scripts/test.sh
```

Fresh result at committed HEAD `fff0f306`:

- Exit code: `0`
- Failed: `0`
- Passed: `5,214`
- Skipped: `2`
- Total: `5,216`
- Wall time: `26s`, below the enforced `30s` ceiling.

This proves the new docs guard participates in the default fast suite without breaking existing
Miller behavior or test conventions. `git diff --check` also passed.

## Acceptance criteria

- [x] **No forbidden mechanism:** The contract explicitly rejects PID/state/token/HTTP/port,
  workspace/index/watcher, database, self-update, and broker-initiated restart mechanisms. The
  broker owns embedding compute only.
- [x] **Frozen sidecar protocol remains separate:** The contract consumes and links
  `docs/contracts/semantic-sidecar-protocol-v1.md`; it does not alter that protocol or a sidecar
  file.
- [x] **Ownership supersession is explicit:** ADR-0003 and both named historical designs point to
  `semantic-broker-v1` and state that it supersedes process-local resident-child ownership.
- [x] **Focused guard passes:** 3/3 tests passed at committed HEAD.
- [x] **No new MCP tool:** The contract states the prohibition and no runtime/tool surface changed.
- [x] **Off remains zero work:** Broker paths and resources are derived only after semantic
  activation is enabled.
- [x] **Release boundary preserved:** No push, publish, tag, or release occurred; releases remain
  approval-gated.

## Architecture quality

**Affected modules:** Documentation contracts and a file-content guard only.

**Caller-facing interface:** The new contract is smaller than the later runtime behavior it
unlocks: frozen protocol envelopes remain unchanged while lifecycle, IPC, ownership, scheduling,
security, and failure policy are specified separately.

**Depth/locality check:** No runtime code, sidecar code, vector artifact, MCP surface, or extraction
surface changed.

**Test surface:** Tests read the same published Markdown contract later implementers and reviewers
consume.

**Seams/adapters:** No runtime seam was introduced in this task.

**Rejected shortcuts:** Per-process stdio fallback, a general machine daemon, PID/state files,
HTTP/token control planes, model duplication after spawn races, unbounded queues, and untyped OOM
demotion.

**Architecture risk:** Low for this documentation-only slice; the frozen contract governs later
high-risk cross-process implementation.

## Miller evidence

Every Miller call made for this task and the shape it proved:

1. `workspace onboarding(path=<worktree>)` — proved the worktree was not registered and required
   an explicit `workspace open`; no code assumption was made from the failed onboarding.
2. `workspace open(path=<worktree>)` — registered and primed the exact worktree index at revision
   1.
3. `context(query="Contract-first documentation and test slice ...")` — located the existing
   process-scoped `SemanticEmbeddingSessionBroker` and semantic implementation area; disposition
   was partial, so targeted doc/test discovery followed.
4. `search(query="semantic broker ownership sidecar lifecycle", mode=content, docs/**)` — proved
   the implementation-plan prose is the only existing broker-oriented document before this task.
5. `inspect(ADR-0003...)` — proved the ADR path exists and is Markdown with no code-symbol API.
6. `inspect(2026-07-19...design.md)` — proved the historical program-design path exists.
7. `inspect(2026-07-21...design.md)` — proved the repair-design path exists.
8. `inspect(docs/README.md)` — proved the current documentation-map path exists.
9. `search(...ContractTests RepoFile File.ReadAllText..., mode=source)` — no combined phrase hit;
   this prevented inventing a test helper.
10. `search(RepoFile, tests/Miller.Tests/Docs/**)` — proved no existing `RepoFile` helper in the
    requested test area.
11. `search("docs contracts documentation guard", tests/Miller.Tests/Docs/**)` — proved no
    pre-existing Docs contract-test convention at that path.
12. `search(File.ReadAllText, mode=source, tests/Miller.Tests/**)` — proved file-content contract
    guards use direct `File.ReadAllText`.
13. `search("docs/contracts/", mode=source, tests/Miller.Tests/**)` — proved existing tests assert
    public contract paths.
14. `search(AppContext.BaseDirectory, mode=source, tests/Miller.Tests/**)` — located existing
    repo-root resolution patterns rather than guessing one.
15. `search("tests/Miller.Tests/Docs", mode=file)` — proved the Docs test directory did not yet
    exist.
16. `search(RepoRoot, tests/Miller.Tests/**)` — resolved the public
    `ScaleTestSupport.RepoRoot()` helper and its test callers.
17. `search(Miller.slnx, mode=source, tests/Miller.Tests/**)` — proved the repository-root sentinel
    used by the helper.
18. `search("docs/adr/", mode=source, tests/Miller.Tests/**)` — found the existing docs/ADR
    reference convention in `AgentInstructionsTests`.
19. `search(LocateRepoRoot, tests/Miller.Tests/**)` — resolved the helper's implementation and
    tests.
20. `inspect(MillerExtractContractTests.cs)` — listed the existing contract-test class and
    fact/theory organization before reading its convention.
21. `inspect(MillerExtractContractTests, depth=overview)` — proved its public xUnit class/fact
    surface.
22. `inspect(Miller.Tests.csproj)` — proved the project is an XML test project with no indexed
    code symbols; the bounded file read then confirmed xUnit v3 and default fast filtering.
23. `inspect(ScaleTestSupport.RepoRoot, depth=full)` — proved the exact public, zero-argument
    helper and its multi-fallback behavior.
24. `impact(changed_paths=[six Task 1 paths])` before editing — classified the four existing docs
    as having no graph dependents and the two new paths as not yet indexed; risk was documentation
    local.
25. `workspace refresh` after editing — indexed the new contract/test at revision 2.
26. `inspect(SemanticBrokerContractTests.cs)` — listed the three public facts and shared contract
    path.
27. `inspect(SemanticBrokerContractTests, depth=full)` — proved the complete caller-facing test
    shape and exact use of `ScaleTestSupport.RepoRoot()`.
28. `search(exact identity prefix, mode=content, semantic-broker-v1.md)` — proved the normative
    identity input is present in the published contract.
29. `search(process-local supersession phrase, mode=content)` — returned the ADR and historical
    design supersession text outside the brace-style file glob, proving the prose while exposing
    the glob mismatch rather than treating it as absence.
30. `impact(git=true)` after editing — seeded all tracked documentation changes and reported no
    dependent runtime symbols; parent-owned orchestration files were unseeded.
31. `impact(git=true, base=HEAD^)` after commit — seeded all six Task 1 contract/test paths and
    confirmed no runtime dependents or additional test candidates.

## Commit scope

Committed Task 1 files:

- `docs/contracts/semantic-broker-v1.md`
- `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- `docs/plans/2026-07-21-semantic-production-readiness-repair-design.md`
- `docs/README.md`
- `tests/Miller.Tests/Docs/SemanticBrokerContractTests.cs`

Sole system-required ownership extension:

- `.memories/2026-07-28/030241_c802.md` — Goldfish pre-commit checkpoint, explicitly approved by
  the lead for inclusion because project instructions require checkpoints to ride with commits.

No extra product, runtime, sidecar, plan, progress, brief, or orchestration file was committed.
This report is intentionally written after the commit so it can record the SHA; it is an
uncommitted SDD handoff artifact for the lead.

## Review fix round 1

Lead inline review identified four contract ambiguities. They are fixed in commit
`4fcca1020b9b3ad71c6bfa6cd4acafb798c53b08`
(`docs: tighten semantic broker invariants`).

### Corrections

- **Ownership vocabulary:** `owner` now means only the spawning Miller factory/process that holds
  stdin and Windows Job ownership. The sidecar is the `service broker` / `service-lock holder`;
  it holds the model service lock and, when accelerated, the accelerator lock.
- **Queue saturation:** A full queue returns the existing protocol-v1 `internal_error` envelope.
  The contract forbids a new method, field, or error code for saturation.
- **Owner EOF:** The watcher is armed before model load, and stdin EOF must terminate the broker
  even when load is blocked. Cooperative cancellation is preferred; process-fatal exit is
  permitted for non-cancellable load so the OS releases locks and no orphan remains.
- **Identity and privacy:** The ADR and both supersession designs use
  `broker-contract/protocol/model identity`. Diagnostics, logs, and telemetry forbid query,
  document, and source text; workspace paths; symbols; snippets; vectors; and authentication
  material.

### Review TDD evidence

Focused command:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticBrokerContractTests
```

RED before documentation fixes:

- Exit code: `1`
- Failed: `4`
- Passed: `3`
- Each new review assertion failed on its intended missing guarantee: owner/service-lock
  separation, frozen `internal_error`, fatal EOF/privacy, and complete identity vocabulary.

GREEN at committed HEAD `4fcca102`:

- Exit code: `0`
- Failed: `0`
- Passed: `7`
- Skipped: `0`

Worker ceiling at committed HEAD:

- `scripts/test.sh`
- Exit code: `0`
- Failed: `0`
- Passed: `5,218`
- Skipped: `2`
- Total: `5,220`
- Wall time: `25s`, below the `30s` ceiling.

### Review Miller evidence

1. `inspect(SemanticBrokerContractTests, depth=full)` proved the pre-review seven-path contract
   test surface and exact helper use.
2. Four targeted content searches proved the ambiguous owner sentence, generic queue-error
   wording, overstated orderly EOF guarantee, and incomplete privacy vocabulary at current HEAD.
3. `impact(changed_paths=[contract, ADR, two designs, test])` reported a documentation/test-local
   change with no runtime dependents.
4. `search(owner, semantic-broker-v1.md)` plus the bounded exact-text audit located all
   owner-sensitive clauses before editing.
5. `workspace refresh` indexed the corrected contract/test at revision 4.
6. Post-edit `inspect` proved all seven fact methods; searches proved service-broker ownership,
   fatal EOF, complete privacy, and complete identity vocabulary.
7. The broad queue search missed because Miller content search is literal; the required follow-up
   `search(internal_error, mode=content)` proved the existing protocol error and no-new-code clause.
8. `impact(git=true)` after edits and `impact(git=true, base=HEAD^)` after commit both reported no
   runtime dependents; the five owned task paths were seeded.

### Review commit scope

Committed owned files:

- `docs/contracts/semantic-broker-v1.md`
- `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- `docs/plans/2026-07-21-semantic-production-readiness-repair-design.md`
- `tests/Miller.Tests/Docs/SemanticBrokerContractTests.cs`

System-required checkpoint:

- `.memories/2026-07-28/031104_b3e8.md`

Lead-owned SDD brief/progress, the implementation plan, and this report were not included in the
fix commit.

## Concerns

- Real Windows named-pipe/Job Object behavior and Windows/NVIDIA VRAM exhaustion are intentionally
  not claimed by this contract-only task; later implementation and soak tasks must prove them.
- The fast suite completed close to its 30-second tripwire (25 seconds at current HEAD), though
  it passed.

## Review fix round 2

Grok's progress review found one remaining ownership-terminology contradiction. It is fixed in
commit `3d0e61731ac629a91a915f7ff38192d76e87f120`
(`docs: separate broker service arbitration`) on branch
`codex/shared-semantic-broker-plan`.

### Correction

- Miller owner recovery now occurs through factory lifecycle: a new Miller process establishes the
  owner stdin lease and Windows Job Object, then starts a service-broker contender.
- The model service lock separately arbitrates only which sidecar service broker may load and
  serve.
- The historical integration-design summary uses the same division and describes losing factories
  as polling rather than acquiring ownership through the service lock.

### TDD and verification

Focused command:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticBrokerContractTests
```

RED:

- Exit code: `1`
- Failed: `1`
- Passed: `7`
- The new test failed because the contract lacked the required factory-lifecycle sentence; the
  seven previous contract guards stayed green.

GREEN at committed HEAD `3d0e6173`:

- Exit code: `0`
- Failed: `0`
- Passed: `8`
- Skipped: `0`

Fast suite at committed HEAD:

- Exit code: `0`
- Failed: `0`
- Passed: `5,219`
- Skipped: `2`
- Total: `5,221`
- Wall time: `25s`, below the `30s` ceiling.

### Miller evidence

- Pre-edit content searches proved both misleading phrases exactly.
- `inspect(BrokerContract_SeparatesFactoryRecoveryFromServiceBrokerArbitration, depth=full)` proved
  the new caller-facing test surface.
- Post-edit searches proved factory-lifecycle recovery in the contract and sidecar-only service
  arbitration in both docs.
- Pre-edit, post-edit, and committed-diff impact checks seeded all three owned paths and reported
  no runtime dependents.

### Commit scope and dirty state

Committed:

- `docs/contracts/semantic-broker-v1.md`
- `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- `tests/Miller.Tests/Docs/SemanticBrokerContractTests.cs`
- `.memories/2026-07-28/031810_513f.md`

After the commit, no task implementation file is dirty or staged. The remaining dirty paths are
lead-owned SDD orchestration artifacts:

- `.razorback/sdd/progress.md`
- `.razorback/sdd/task-1-brief.md`
- `.razorback/sdd/task-1-report.md`
- `.razorback/sdd/task-2-brief.md`
- `.razorback/sdd/task-3-brief.md`
