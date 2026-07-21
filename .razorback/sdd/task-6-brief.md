### Task 6: Content-route canary

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (content route — `RunContentCorpus` :1252 and its dispatch site)
- Test: `tests/Miller.Tests/Server/CanaryContentSearchTests.cs` (new)

**Interfaces:**
- Consumes: Task 5's orchestration helper and classifier; the content-route semantic arm (`SemanticTextArm`) with Task 3 diagnostics.
- Produces: `op=content` canary rows. Served-result hashes: path array only (content rows are path+line chunks, not symbols — name/qualified arrays are absent per the absent-vs-zero rule). Treatment = the P3 content-mode hybrid arm forced past the mode gate; control = lexical content search byte-identical.

**Contract inputs:** Same field table and eligibility ladder as Task 5. `query_class` for content queries still comes from `CanaryQueryClassifier` (op=content promotes prose → docs_like).

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs` (content route), `tests/Miller.Tests/Server/CanaryContentSearchTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same file as Task 5 (`SearchTool.cs`); builds on its orchestration helper.

**What to build:** Extend the canary to the fourth instrumented surface. The content route renders path+snippet rows outside the symbol-candidate seam, so the arm split and stamping hook into the content dispatch instead.

**Acceptance criteria:**
- [ ] Content-op canary rows carry path hashes only; name/qualified arrays absent.
- [ ] Control/off byte-identical to today's content output; treatment identical to `MILLER_SEMANTIC=on` content hybrid.
- [ ] Worker-scope verification passes and the change is committed per commit mode.

