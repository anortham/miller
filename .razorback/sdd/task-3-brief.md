### Task 3: `miller semantic prepare` CLI verb (consented model download)

**Files:**
- Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs`

**Interfaces:**
- Consumes: sidecar `prepare [--model <id>]` subcommand (downloads+verifies into the shared cache; machine-readable progress on stdout; concurrent-safe via cache lock — mechanics all sidecar-owned per §4.4); sidecar binary resolution at `<ToolsRoot>/julie-semantic-sidecar[.exe]` (same as `VectorConvergeService.cs:794`); `DiskPreflight` from Task 2 (compile-time only — if Task 2 hasn't landed when this dispatches, preflight wiring moves to Task 4's lane-2 slot and this task notes the mismatch).
- Produces: CLI verb `miller semantic prepare [--model <id>] [--json]` — exit 0 on prepared (fresh or already-cached), nonzero with the sidecar's actionable message on failure (offline, sha mismatch, disk). A workspace-local marker file contract for Task 4: `<workspace>/.miller/semantic-prepare.marker` created before the child starts (content: model id + pid + ISO timestamp), always deleted on exit (finally). Verb help text registered in CLI `help`.

**Contract inputs:** Design §4.4 verbatim: "Miller's `miller semantic prepare` CLI verb … shell[s] out to the sidecar's `prepare`; consent semantics live in Miller, mechanics in the sidecar." Running the verb IS the consent act — Miller never auto-downloads; the converge path keeps its existing stated-refusal behavior on `model_not_prepared`.
**MCP-stinginess check:** CLI verb only; no MCP tool or param is added.

**File ownership:** Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Modify: `src/Miller.Server/Cli/CliDispatch.cs`; Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 4 consumes Task 3's marker contract; ordered lane.

**What to build:** The explicit, consented model-acquisition entry point. Streams the sidecar's progress through to the console (CLI owns stdout — no Serilog), runs a disk preflight against the model cache target before launching, and reports `model_not_prepared` remediation in `workspace status` messaging ("run `miller semantic prepare`") if that hint is not already present.

**Approach:** Follow the existing CLI verb pattern in `CliDispatch` (branch at the verb table, ~`CliDispatch.cs:92-148`; no host build, no index load — like `version`). Fake the sidecar in tests with a stub executable script/`FakeSemanticSidecar`-style process (existing support under `tests/Miller.Tests/Support/`); tag `Scale` if it must spawn a real process — prefer a pure argument/marker/exit-code core (`SemanticPrepareCli.Run(...)` with an injected process runner) so the fast suite covers logic without spawning.

**Acceptance criteria:**
- [ ] `miller semantic prepare` shells to the pinned sidecar's `prepare`, streams progress, and returns the sidecar's exit status; `--model` passes through.
- [ ] Marker file exists exactly while the child runs (created before spawn, removed on success, failure, and cancellation).
- [ ] Missing sidecar binary fails loud with the restore-script message (same wording pattern as `CliDispatch.cs:529`).
- [ ] Disk preflight refusal produces an actionable message and nonzero exit without spawning the child.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

