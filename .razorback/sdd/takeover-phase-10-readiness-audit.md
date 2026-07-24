# Phase 10 execution-readiness audit

**Date:** 2026-07-23  
**Mode:** read-only audit; no source edits, paid agent runs, sealed material inspection, push, package
dispatch, release, or Julie-session interference  
**Worktree:** `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`  
**Branch:** `codex/miller-julie-takeover`  
**Audited HEAD:** `ac493196f2c35ce1b7fb6d5d1f91365e98a11359`  
**Merge base:** `d21e359e79e9f5b65b6ae38aab034a18f8d01f57` (`main`)  

## Execution update — 2026-07-24

- Phases 7–9 are complete. Miller's CodeRank evaluation lane is committed at `6f2234d8`; the frozen visible
  comparison retained BGE-small and did not open the model-selection sealed gate.
- `julie-extract 2.17.0` is released and pinned from its four public packages. The all-language resolution
  gate covers 36 languages and 689 coverage cells with zero silent cells or deferred coverage debt.
- `julie-semantic-sidecar v0.1.0-rc.4` is released and pinned from four reproducible public packages. The exact
  Apple arm64 archive passed CPU and Metal conformance, concurrent determinism, fallback, and throughput proof;
  Apple x64 Metal, Linux Vulkan, and Windows Vulkan remain honestly labeled package candidates until their
  applicable physical-hardware promotion evidence exists.
- Package smoke now asserts all nine MCP tool names. The strict hash-bound Miller product attestation, operator
  creation command, fail-closed receipt validator, conditional Miller migration guide, and conditional 1.14.0
  release notes are integrated through `d0fe8cf3`.
- Miller's public-pin/runtime integration is committed at `450cda06`; fast, Scale, Release, Native AOT, real
  semantic search, and packaged sidecar/sqlite-vec smoke gates pass. Three fresh Claude integration reviews
  found and closed the release-manifest issues; the final review returned zero findings.
- Julie's active session remains untouched. Its retirement/support-window change is a separate repository
  integration boundary and is not required to mutate the frozen Miller candidate.
- The spend-once final takeover lane remains unspent. All privacy prohibitions below remain in force.

## Verdict

Phase 10 is ready for candidate freeze, visible calibration, the nine tool reviews, broad review, and local
gates. It is not ready for the spend-once sealed lane until those gates and the approval-gated package workflow
pass on one unchanged commit.

The safe order is:

1. close Phases 7–9 and every known platform/package gap;
2. prepare the conditional replacement/retirement documentation without publishing it;
3. freeze the Miller and Julie candidates;
4. pass evaluator tests and full visible calibration;
5. complete all nine tool-specific Claude reviews, the broad Claude review, dispositions, fixes, and reruns;
6. pass local, plugin, CLI/MCP, and remote four-platform package gates on the final frozen candidate;
7. run the operator-controlled sealed decision once;
8. accept only the safe aggregate plus a privacy-safe product-verdict attestation;
9. if every gate passes, merge the already-reviewed candidate and activate the prepared Julie retirement
   documentation.

The current Phase 10 wording cannot be executed literally:

- Claude reviewers may not inspect “sealed evaluator rows.” The frozen evaluator contract and operator protocol
  prohibit implementation or reviewer sessions from seeing sealed prompts, labels, task rows, answers, evidence,
  trajectories, mappings, or scorer rows.
- Reviews cannot safely follow the sealed run. Any accepted review finding that changes code, contracts, prompts,
  schemas, packages, or adapter identity invalidates the spend-once candidate identity.
- The safe aggregate intentionally hides the adapter-to-product mapping. It does not by itself prove that the
  neutral winning role is Miller.
- The former eight-tool package assertion is resolved: package smoke now requires all nine MCP tools, including
  `patterns`.
- Miller’s build version includes the git short SHA. Adding replacement documentation after the sealed run changes
  the packaged binary identity even when runtime source is unchanged. Final conditional docs must be part of the
  frozen, reviewed candidate before the sealed run.

## Current prerequisite state

- The takeover branch contains the complete Phase 0 evaluator and Phases 1–9, plus Phase 10 readiness,
  attestation, migration, release-note, extractor-pin, and sidecar-pin work.
- The final model decision retains BGE-small as production default and keeps CodeRank evaluator-only.
- The RC4 package matrix includes macOS arm64, macOS x64, Linux x64 Vulkan, and Windows x64 Vulkan.
- Julie main was read-only inspected at `37543a0e126ca24105bc630ca5f04410837cbee6`,
  `main...origin/main`, clean. Its README already declares Maintenance Mode and directs new workflows to Miller.
  No migration, rollback-window, or retirement guide exists. Julie’s active session remains untouched.

Phases 7–9 are closed. Candidate freeze must still bind the visible snapshots, adapter identities, tool schemas,
runtime, selection, and package inputs before calibration begins.

## Corrected executable sequence

### 0. Close prerequisites and prepare conditional docs

Before any paid calibration or sealed execution:

- verify the completed Phase 7 remaining-surface work and Phase 8 all-language extraction coverage;
- verify the completed Phase 9 RC4/macOS-x64/platform/model-decision gates;
- retain `patterns` in both Unix and Windows MCP tool-list assertions in `.github/workflows/release.yml`;
- retain the resolved snapshot-manifest contract and safe product-attestation contract;
- prepare, but do not publish, the Miller replacement text, migration guide, and release notes;
- if harness guidance or plugin skills changed, run `scripts/sync-agents.sh` and
  `scripts/sync-plugin-skills.sh` before freezing;
- reconcile Julie’s active session before creating or modifying any Julie worktree.

Preparing the final wording before the decision is safe because the branches are not merged or published. If the
decision fails, discard or revise the conditional docs; do not publish them.

### 1. Freeze exact candidates

For each product, record and verify:

```bash
git rev-parse HEAD
git status --short --branch
git worktree list
```

Create dedicated immutable benchmark snapshots outside all active developer checkouts. Only top-level `.miller`
and `.julie` artifact directories may be writable or dirty. Freeze:

- source commit and content hashes;
- adapter command, version command, binary path, binary SHA-256, version, and commit;
- allowed environment keys and hashed values;
- per-snapshot workspace, index, vector, and model identities;
- MCP instructions and tool-schema hashes;
- task, snapshot, answer-schema, runtime, and selection identities;
- Codex `0.145.0`, `gpt-5.6-sol`, reasoning `medium`, `tiktoken==0.13.0`, and `o200k_base`;
- limits of 8 MCP calls, 12,000 tool-output tokens, and 120 seconds.

No candidate source, docs, prompt, schema, package, or adapter change is allowed after this freeze.

### 2. Prove the evaluator locally

Create the pinned environment and run all evaluator tests:

```bash
python3 -m venv .venv-agent-efficiency
.venv-agent-efficiency/bin/pip install \
  -r scripts/benchmarks/agent-efficiency/requirements.txt
.venv-agent-efficiency/bin/python -m unittest discover \
  -s scripts/tests -p 'test_*.py'
dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj -c Release
```

The relevant frozen owners are:

- `docs/contracts/takeover-evaluation-v1.md`
- `scripts/benchmarks/agent-efficiency/{README.md,SEALED-AGENT-PROTOCOL.md}`
- `scripts/benchmarks/agent-efficiency/{task-manifest,snapshot-manifest,run-result,answer-schema}.json`
- `scripts/benchlib/{agent_contract,agent_runner,recording_mcp_proxy,reporting}.py`
- `scripts/bench-agent-efficiency.py`
- `eval/retrieval-eval`

### 3. Run full visible calibration

Use all five committed development snapshots, every task, and all 13 capabilities. Full scope forbids
`--task-family`.

```bash
TAKEOVER_PY=.venv-agent-efficiency/bin/python
TAKEOVER_CAL_ROOT=/opt/bench/final-visible

"$TAKEOVER_PY" scripts/bench-agent-efficiency.py \
  --manifest scripts/benchmarks/agent-efficiency/dev-tasks.json \
  --snapshots scripts/benchmarks/agent-efficiency/dev-snapshots.json \
  --snapshot-root goldfish=/opt/bench/snapshots/goldfish \
  --snapshot-root eros=/opt/bench/snapshots/eros \
  --snapshot-root razorback=/opt/bench/snapshots/razorback \
  --snapshot-root tree-sitter-razor=/opt/bench/snapshots/tree-sitter-razor \
  --snapshot-root tree-sitter-c-sharp=/opt/bench/snapshots/tree-sitter-c-sharp \
  --runtime-identity "$TAKEOVER_CAL_ROOT/runtime-identity.json" \
  --arm both \
  --corpus-role calibration \
  --decision-scope full \
  --out "$TAKEOVER_CAL_ROOT/run" \
  --seed 731 \
  --model gpt-5.6-sol \
  --reasoning medium \
  --preflight-only
```

After the preflight identity is reviewed, remove only `--preflight-only` and run the otherwise byte-identical
command. Then score exactly as generated:

```bash
export AGENT_EFFICIENCY_EXPORT=/opt/bench/final-visible/run/exports
export AGENT_EFFICIENCY_PYTHON=.venv-agent-efficiency/bin/python
sh "$AGENT_EFFICIENCY_EXPORT/agent-score-command.txt"
```

The current visible manifest has 15 tasks over five snapshots, all six workflow classes, and all 13 closed
capabilities. A full calibration is implementation evidence only; its `decision_verdict` must remain
`not_decisional`.

If calibration exposes a defect, stop before sealed execution, fix it, refreeze both candidates, and repeat from
Step 1.

### 4. Complete the nine tool-specific Claude reviews

This is a paid external-review boundary. Before the first run:

```bash
which claude
claude --version
claude auth status
```

The executable is currently present as `/Users/murphy/.local/bin/claude`, version `2.1.218`; authentication and
the user’s current subscription/API usage must still be checked immediately before the ten review runs.

Every review uses a fresh, read-only, non-persistent process:

```bash
RAZORBACK_SKILL=/Users/murphy/.codex/razorback/skills
SCHEMA_JSON=$(jq -c 'del(."$schema")' \
  < "$RAZORBACK_SKILL/codex-cli/schemas/review-output.schema.json")

claude -p \
  --no-session-persistence \
  --dangerously-skip-permissions \
  --output-format json \
  --json-schema "$SCHEMA_JSON" \
  --tools "Read,Grep,Glob" \
  --strict-mcp-config \
  --max-turns 15 \
  --max-budget-usd 5.00 \
  "$PROMPT" < /dev/null 2>/dev/null > "$OUTPUT"
```

Do not set a model unless the user explicitly chooses one. `--max-budget-usd` bounds API use but may not cap
subscription usage.

The branch review base is always explicit:

```bash
TAKEOVER_BASE=$(git merge-base HEAD main)
git diff --check "$TAKEOVER_BASE"..HEAD
git diff --find-renames --no-ext-diff "$TAKEOVER_BASE"..HEAD -- <tool-paths>
```

Each prompt receives:

- the exact base and HEAD commits;
- the scoped branch diff and changed-file list;
- the tool implementation and shared modules it consumes;
- the contracts and focused tests in the table below;
- full visible-calibration aggregate evidence and visible rows relevant to the tool;
- no sealed row, prompt, label, answer, evidence, trajectory, scorer row, filename, or mapping.

| Review | Primary paths | Contracts | Minimum focused test filter |
|---|---|---|---|
| `search` | `Miller.Core/Search/**`, search readers/sidecars, `SearchTool.cs`, `SearchRoute*.cs` | takeover v1, tool diagnostics v1 | `SearchTool`, `SearchRoute`, `SymbolReranker`, `SearchRelaxation`, `HybridSearch`, `SearchDeterminism` |
| `inspect` | reference readers, `SmartTargetResolver.cs`, `InspectTool.cs`, continuation | exact-reference consumers, tool diagnostics, tool continuation | `InspectTool`, `ReferenceEvidenceReader`, `SmartTargetResolver`, `ToolContinuation` |
| `context` | graph packing/ranking, reference readers, `ContextTool.cs` | exact-reference consumers, tool diagnostics | `ContextTool`, `ContextPacker`, `ContextPivotRanker`, `LiveContextImpact` |
| `impact` | graph/readers, `TestLinkageReader.cs`, `ImpactTool.cs`, impact CLI | impact traversal, test-role, and revision-delta v1; diagnostics | `ImpactTool`, `SymbolGraph`, `TestLinkageReader`, `ImpactRevisionDeltaCli`, `LiveContextImpact` |
| `trace` | graph/readers, reference evidence, `TraceTool.cs` | trace JSON v1, exact-reference consumers, diagnostics, continuation | `TraceTool`, `ReferenceEvidenceReader`, `SymbolGraphReader`, `ToolContinuation` |
| `edit` | `Miller.Core/Editing/**`, reference evidence, `Edit*.cs` | edit JSON v1, exact-reference consumers | `EditTool`, `EditPlanner`, `RenamePlanner`, `LiveEdit`, `ReferenceEvidenceReader` |
| `content` | content corpus/readers/writers, `ContentTool.cs` | content corpus v1 | `ContentTool`, `ContentCorpus`, `ContentSearchProjection`, `FtsTextContentSearch` |
| `workspace` | registry/binding/health/lifecycle, `WorkspaceTool.cs` | workspace status, health, onboarding, leader, refresh-wait; diagnostics | `WorkspaceTool`, `WorkspaceRender`, `WorkspaceHealth`, `WorkspaceBinding`, `LiveWorkspace` |
| `patterns` | structural-fact reader, `PatternsTool.cs` | patterns JSON v1, diagnostics | `PatternsTool`, `PatternFactsReader`, `PatternMetadataSql` |

Run a focused filter with:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release \
  --filter "FullyQualifiedName~<class-fragment>|FullyQualifiedName~<class-fragment>"
```

Validate each Claude envelope:

```bash
jq -e '.structured_output.findings | type == "array"' "$OUTPUT" >/dev/null
jq '.structured_output.findings[]?' "$OUTPUT"
```

If `.structured_output` is absent, parse `.result` as JSON and validate the same schema. Classify every material
claim as `accepted`, `corrected`, `rejected`, or `unproven`, with local file/test evidence. Any accepted code or
contract finding returns execution to Step 1 after the fix.

### 5. Run the broad Claude review

Only after all nine tool passes and dispositions are clean, run one separate fresh review using the same
read-only/schema command. Its scope is the complete `main...HEAD` branch diff, architecture boundaries, all
contracts, visible evaluator evidence, disposition records, replacement docs, and verification ledgers.

The broad review cannot replace a tool review. It also cannot see sealed rows. Any accepted material fix returns
execution to Step 1 and invalidates all later identities.

### 6. Run local candidate gates

```bash
git diff --check
scripts/test.sh all
dotnet build Miller.slnx -c Release
scripts/test-plugin.sh
cmp -s CLAUDE.md AGENTS.md
diff -qr .agents/skills skills
```

Run the evaluator tests from Step 2 again. The Scale suite must execute, not merely skip; restore the pinned
extractor first if `.tools/julie-extract` is absent.

The two comparisons are read-only gates. If either fails, return to Step 0, run the applicable sync script, and
refreeze; do not let a sync script mutate the already frozen candidate.

The fast suite contains the CLI/tool contracts. For an explicit contract ledger also run:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release \
  --filter "FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~CliDispatchTests|FullyQualifiedName~CliOptionsTests|FullyQualifiedName~CliBinarySubprocessTests|FullyQualifiedName~ToolDiagnostic|FullyQualifiedName~ToolContinuation"
```

The package MCP smoke remains the authoritative AOT `initialize` plus `tools/list` check after it is corrected to
assert all nine names:

```text
search inspect context impact trace edit content workspace patterns
```

### 7. Run the four-platform package gate

This step requires explicit approval to push the frozen branch and use GitHub-hosted build resources. The current
documentation command omits `--ref`; before local merge, the branch gate must target the pushed frozen branch:

```bash
gh workflow run release.yml \
  --ref codex/miller-julie-takeover \
  -f version=<frozen-candidate-version> \
  -f prerelease=false \
  -f publish=false \
  -f allow_overwrite=false
```

Wait for success and download/verify the artifacts. Do not promote or publish. The workflow matrix is:

| Target | RID | Runner |
|---|---|---|
| `aarch64-apple-darwin` | `osx-arm64` | `macos-14` |
| `x86_64-apple-darwin` | `osx-x64` | `macos-15-intel` |
| `x86_64-unknown-linux-gnu` | `linux-x64` | `ubuntu-24.04` |
| `x86_64-pc-windows-msvc` | `win-x64` | `windows-2025` |

Each leg must prove the packaged Miller binary, dashboard/assets, pinned extractor, pinned semantic sidecar,
sqlite-vec KNN roundtrip, version commands, checksums, MCP initialization, and all nine MCP tools.

If plugin manifests or the launcher changed, also run the documented Cursor-style local smoke with
`MILLER_BINARY`:

- from `/` with no workspace environment;
- from a non-Miller repository;
- with `${CURSOR_PLUGIN_ROOT}` expanded to the package root;
- requiring `initialize` and `tools/list`.

RC4 supplies the macOS x64 package required by this gate.

### 8. Run the sealed paired decision once

This is an external operator and paid-compute boundary. The implementation/review session supplies no private
task data and must not inspect the operator root.

The operator places the sealed task manifest, snapshot manifest, runtime identity, immutable snapshots, raw output,
and exports beneath one external root. Exactly 30 tasks are required: five for each of the six workflow classes,
covering all 13 capabilities and at least five repository/language families.

The operator first runs:

```bash
TAKEOVER_PRIVATE_ROOT=/operator/private/takeover-v1
TAKEOVER_PY=/operator/venv/bin/python

"$TAKEOVER_PY" scripts/bench-agent-efficiency.py \
  --manifest "$TAKEOVER_PRIVATE_ROOT/task-manifest.json" \
  --snapshots "$TAKEOVER_PRIVATE_ROOT/snapshot-manifest.json" \
  --snapshot-root <repo-id>=<immutable-snapshot-root> \
  --runtime-identity "$TAKEOVER_PRIVATE_ROOT/runtime-identity.json" \
  --arm both \
  --corpus-role decision \
  --decision-scope full \
  --private-root "$TAKEOVER_PRIVATE_ROOT" \
  --out "$TAKEOVER_PRIVATE_ROOT/run" \
  --seed 731 \
  --model gpt-5.6-sol \
  --reasoning medium \
  --preflight-only
```

Repeat `--snapshot-root` exactly once for every repository in the private snapshot manifest. Full decision scope
accepts no `--task-family`. After reviewing the private preflight, the operator removes only
`--preflight-only` and runs the otherwise identical command.

The controller owns balanced order, one-versus-three repetitions, whole-pair void reruns, resume identity, and
artifact verification. Product errors, invalid answers, disallowed tools, timeouts, and budget failures are scored
outcomes, not voids.

The operator then runs the generated command exactly:

```bash
export AGENT_EFFICIENCY_EXPORT="$TAKEOVER_PRIVATE_ROOT/run/exports"
export AGENT_EFFICIENCY_PYTHON="$TAKEOVER_PY"
sh "$AGENT_EFFICIENCY_EXPORT/agent-score-command.txt"
```

The generated command is contractually:

```bash
dotnet run --project eval/retrieval-eval/RetrievalEval.csproj -- decision-score \
  --tasks "$AGENT_EFFICIENCY_EXPORT/agent-tasks.jsonl" \
  --baseline "$AGENT_EFFICIENCY_EXPORT/baseline-results.jsonl" \
  --candidate "$AGENT_EFFICIENCY_EXPORT/candidate-results.jsonl" \
  --decision-scope full \
  --out "$AGENT_EFFICIENCY_EXPORT/aggregate.json" &&
"${AGENT_EFFICIENCY_PYTHON:-.venv-agent-efficiency/bin/python}" \
  scripts/bench-agent-efficiency.py finalize-safe \
  --exports "$AGENT_EFFICIENCY_EXPORT" \
  --safe-output "$AGENT_EFFICIENCY_EXPORT/safe-aggregate.json"
```

### 9. Accept only the safe return

The current safe aggregate has exactly these top-level keys:

```text
contract_id
schema_version
corpus_role
decision_scope
parent_manifest_sha256
snapshot_manifest_sha256
runtime_identity_sha256
selection_sha256
selected_capability_ids
selected_task_count
completion
outcome_counts
relevance
correctness
efficiency
baseline
candidate
failure_counts
by_workflow
by_capability
by_repo
by_language
action_verdict
unresolved_void_count
private_evidence_sha256
decision_verdict
```

A valid decision return has:

- `contract_id=takeover-evaluation-v1`, `schema_version=1`;
- `corpus_role=decision`, `decision_scope=full`;
- `selected_task_count=30` and all 13 capability IDs;
- `unresolved_void_count=0`;
- only five-task-floor public workflow/capability groups;
- stripped dynamic repository/language labels;
- ordinal retained hashes such as `artifact_001`;
- `decision_verdict=pass|fail`.

The operator may additionally state only that preflight, automatic reruns, artifact verification, and zero
unresolved voids passed. Do not return `aggregate.json`, scorer JSONL, identity/evidence manifests, private
filenames, raw rows, the void ledger, or adapter mapping.

## Required product-verdict attestation amendment

The safe aggregate is intentionally neutral and hides product mapping. The implementation session therefore
cannot prove that a passing `candidate` is Miller. Before the sealed run, amend the frozen operator protocol to
permit one additional privacy-safe attestation bound to the safe aggregate:

```json
{
  "attestation_contract_id": "takeover-product-verdict-v1",
  "safe_aggregate_sha256": "<64 lowercase hex>",
  "product_under_test": "Miller",
  "product_verdict": "pass",
  "mapping_frozen_before_preflight": true,
  "mapping_changed": false,
  "preflight_passed": true,
  "automatic_reruns_complete": true,
  "artifact_verification_passed": true,
  "unresolved_void_count": 0
}
```

Allowed `product_verdict` values are `pass` and `fail`; it must equal the sealed decision applied by the operator
through the pre-frozen private mapping. The attestation reveals no neutral-role mapping, task data, metrics, paths,
filenames, adapter commands, or evidence. Its safe-aggregate hash binds it to the exact returned file.

This is not allowed by the current “only safe aggregate plus statement” protocol. It must be explicitly approved,
documented, schema-validated, and frozen before the spend begins. Do not improvise it afterward.

## Evaluator drift against Phase 0

The current evaluator implementation remains materially bound to the Phase 0 chain:

| Commit | Frozen responsibility |
|---|---|
| `87bf18ab` | evaluator contract |
| `72d19474` | typed takeover semantics |
| `5a9fd24d` | evidence contract |
| `8290d7ef` | canonical action targets |
| `6593fe6e` | action ontology |
| `145e8d2c` | visible calibration and generated-finalizer path correction |

Since `6593fe6e`, evaluator behavior changed only in `145e8d2c`: the generated finalizer command now uses the
repository-relative script and `AGENT_EFFICIENCY_PYTHON` instead of an absolute controller path. The later
`c6895c78` change affects only the older foundation reference-count helper, not takeover-v1 selection, execution,
or scoring.

One Phase 0 ambiguity must be closed before sealed execution:

- the frozen prose says every takeover manifest carries `contract_id=takeover-evaluation-v1`;
- `snapshot-manifest.schema.json` and `dev-snapshots.json` contain only `schema_version` plus `snapshots`;
- the snapshot loader therefore hashes and validates snapshot identity without a snapshot-level `contract_id`.

This contradiction existed in `87bf18ab`; it is not later drift. Either clarify in the frozen contract that the
snapshot manifest is schema-versioned and bound through the takeover selection identity, or add the contract ID
with a versioned schema migration. Do not silently infer the intended rule during a sealed run.

The task schema also permits a missing top-level `contract_id` for explicit legacy calibration compatibility, but
`build_selection` rejects any full takeover parent whose tasks are not stamped v1. The current
`dev-tasks.json` is correctly stamped and covers all 13 capabilities.

## Final gate and stop rules

Miller is ready for local merge only when all of these are true:

- every prerequisite phase gate is closed;
- visible calibration is complete under the frozen final identity;
- all nine Claude reviews and the broad review have validated disposition records;
- no accepted review finding remains;
- fast, Scale, Release build, evaluator, CLI/MCP, plugin, sync, and four-platform package gates pass;
- package smoke proves all nine MCP tools;
- the sealed safe aggregate has `decision_verdict=pass`, zero unresolved voids, and every correctness, relevance,
  efficiency, wrong-action, and privacy gate passes;
- the approved product attestation says the verdict applies to Miller;
- no P0/P1 correctness blocker remains;
- every Julie-only workflow is ported, explicitly rejected with evidence, or assigned to another component;
- the final worktrees are clean and the exact reviewed/package/sealed identities still match.

If visible calibration, a review, or a package gate fails, fix it before sealed execution and restart from the
candidate freeze. If the sealed gate fails, do not inspect private rows and do not patch under the spent identity.
The operator may return only the aggregate blocker; any new sealed corpus or second spend requires a new user
decision.

## Replacement and retirement documents

Prepare and review these before the final candidate freeze:

### Miller

- `README.md`: replace the current family-level “1.0 replacement story” with the evidence-backed primary-product
  statement while preserving `julie-extractors` and Eros ownership boundaries.
- `docs/README.md`: point to the final decision finding, migration guide, and current release notes.
- new migration guidance: Julie tool/workflow to Miller tool/workflow mappings, install/config changes, workspace
  rebuild behavior, semantic opt-out, unsupported/rejected Julie behaviors, rollback procedure, and support window.
- `docs/release-notes/v<version>.md`: candidate version, pinned extractor/sidecar, migration notes, verification,
  platform evidence, and safe decision summary.
- final finding: only the safe aggregate, product-verdict attestation, review dispositions, and non-private gate
  evidence.

### Julie

Julie already says Maintenance Mode. Its conditional retirement change should add:

- retirement effective version/date;
- last-supported Julie release line;
- Miller migration link;
- rollback window and exact reinstall/re-enable procedure;
- security/critical-fix policy during the window;
- explicit statement that Julie is retained during rollback and is not deleted by the benchmark;
- ownership boundary for `julie-extractors`.

Julie changes require its active session to finish or explicitly hand off cleanly. They are a separate repository
integration and push/release approval boundary. No obsolete Julie MCP surface needs a deprecation marker; remove a
surface outright only in a separately approved retirement change.

## Autonomous versus external work

| Work | Can run autonomously after prerequisites? | Boundary |
|---|---|---|
| evaluator tests, visible preflight, local build/tests, plugin/sync gates | yes | no paid calls and no external mutation |
| visible paired execution | no | Codex compute/spend approval |
| nine Claude reviews plus broad review | no | authentication and paid/subscription usage approval |
| sealed preflight/execution/scoring | no | external operator, private artifacts, spend-once approval |
| safe aggregate validation | yes | only after operator returns the allowlisted file |
| product-verdict attestation | no | contract amendment and operator statement |
| push frozen branch and package workflow | no | push approval and GitHub-hosted compute |
| local merge to Miller main | no | explicit integration approval after exact state check |
| Julie docs/merge/push/release | no | active-session reconciliation plus separate cross-repo approvals |
| publish/tag/release | no | explicit release approval; outside merge-readiness |
