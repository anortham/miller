### Task B5: Shadow generations, promote, rollback, corruption recovery

**Files:**
- Create: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`, `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`
- Modify: `src/Miller.Indexing/Semantic/VectorStore.cs`, `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs` (per-generation recovery registration)

**Interfaces:**
- Consumes: vectors-v1 §Shadow generations and rollback (generation tag, compatible vs incompatible promotes, lifecycle, GC rules), §Corruption recovery; `FullRebuildPromotion` as the promote-pattern reference.
- Produces: shadow build at `vectors.db.rebuild` → atomic promote; incompatible promote retains superseded generation as self-contained `vectors.gen-<tag>.db`; reader routing to a retained compatible generation across restarts; GC honoring the three never-delete rules (only ready generation, soak window, live compatible reader); corrupt vectors delete+rebuild without touching symbols.db.

**Contract inputs:** vectors-v1 conformance clause 6 verbatim.

**File ownership:** Create: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`, tests; Modify: `src/Miller.Indexing/Semantic/VectorStore.cs`, `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs`

**Serialization required:** Yes

**Dependency reason:** Promote/GC operate on B4's written artifact.

**What to build:** Generation lifecycle: shadow beside live, promote, retain, serve-from-retained, GC — plus corruption recovery wiring.

**Acceptance criteria:**
- [ ] Incompatible promote: old generation discoverable and queryable by an old-fingerprint reader across a process restart (Scale test)
- [ ] GC never deletes the only ready generation, an in-soak generation, or one with a registered live reader (each rule its own test)
- [ ] Corrupt vectors.db ⟹ deleted + rebuilt via recovery path; symbols.db untouched
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

