### Task B1: MILLER_SEMANTIC activation, VectorSidecar skeleton, off-guarantee

**Files:**
- Create: `src/Miller.Indexing/SemanticActivation.cs`, `src/Miller.Indexing/VectorSidecar.cs`, `tests/Miller.Tests/Indexing/SemanticActivationTests.cs`, `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`, `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs` + the render seam that carries sidecar facts (mirror how search-sidecar facts flow; discover via WorkspaceFactsAssemblerTests)
- Test: `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`

**Interfaces:**
- Consumes: `SymbolSearchSidecar` (src/Miller.Indexing/SymbolSearchSidecar.cs:12) as the exact structural pattern; vectors-v1 §File placement and activation + §Status vocabulary.
- Produces: `SemanticActivation.FromEnvironment()` → `off | shadow | on` (`0` aliases `off`); `VectorSidecar` with `EnvVar`, `Disabled` singleton, `PathFor(workspaceRoot)`, `TryOpen`, `OpenRequired` (fail-visible message includes "run `miller workspace refresh`"), no real vec0 yet — B2 adds the store; `vectors:` status line in workspace status/health with vocabulary `disabled | unavailable (reason)` for now (later states land with B2–B6).

**Contract inputs:** vectors-v1 §File placement and activation (the off-guarantee definition, verbatim), §Status vocabulary. Off means: no open, no create, no stat, no `vectors.gen-*.db` enumeration, status `disabled` derived without filesystem access.

**File ownership:** Create: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Indexing/SemanticActivation.cs`, `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`, `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`; Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, render seam, their tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The activation switch, the sidecar shell mirroring SymbolSearchSidecar, and the test-enforced zero-work guarantee — the contract clause everything later builds under.

**Approach:** The off-guarantee test observes the filesystem (e.g. workspace dir with a sentinel `vectors.gen-x.db` whose access would be detectable, plus asserting no vectors.db created and no path enumerated via an injected filesystem probe or directory watcher — choose the strongest observable the codebase supports). Status rendering follows the search-sidecar fact pattern exactly.

**Acceptance criteria:**
- [ ] `MILLER_SEMANTIC` unset/`off`/`0` ⟹ status `disabled`, zero filesystem touches under the vectors paths (test-enforced)
- [ ] `shadow`/`on` with no artifact ⟹ `unavailable (reason)` fact via WorkspaceFactsAssembler in compact + JSON
- [ ] VectorSidecar mirrors SymbolSearchSidecar's surface (EnvVar/Disabled/TryOpen/OpenRequired) with fail-visible messaging
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

