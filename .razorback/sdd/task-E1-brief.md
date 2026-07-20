### Task E1: MinHash/LSH near-duplicate analyzer

**Files:**
- Create: `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs`, `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs`
- Modify: `src/Miller.Server/Tools/MetricsTool.cs` (RunClones, src/Miller.Server/Tools/MetricsTool.cs:120), `src/Miller.Server/Cli/CliDispatch.cs` metrics verb only if flag plumbing requires
- Test: `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: symbol bodies/body hashes as `CloneGroupReader` reads them today (same source columns; discover exact query via `CloneGroupReader`, src/Miller.Indexing/CloneGroupReader.cs:5).
- Produces: `NearDuplicateAnalyzer.FindGroups(inputs, options) -> IReadOnlyList<NearDuplicateGroup>` (group members + Jaccard-estimate similarity), pure and deterministic; `metrics clones` output gains `kind=near_duplicate` groups with similarity alongside existing exact groups (exact groups byte-stable when no near-duplicates exist); JSON per `metrics-json-v1.md` additive rules.

**Contract inputs:** Design §6.4 metrics-clones. Fixed normalization (identifier/whitespace canonicalization), fixed shingle size, fixed seed set, fixed LSH band/row params — all constants in Miller.Core, documented in the analyzer's doc comment. Exact `CloneGroupReader` is untouched.

**File ownership:** Create: `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs`, `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs`; Modify: `src/Miller.Server/Tools/MetricsTool.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Token-shingle MinHash/LSH Type-2 near-duplicate detection surfaced through `metrics clones` as a new group kind. Pure logic in Miller.Core; MetricsTool wires data in.

**Acceptance criteria:**
- [ ] Analyzer is deterministic across runs and platforms (seeded, no Random/time), proven by a repeat-run test
- [ ] Detects Type-2 clones (renamed identifiers/changed literals) in fixture bodies; exact duplicates stay in exact groups, not double-reported
- [ ] `metrics clones` compact + JSON show near_duplicate groups with similarity; existing exact-group output unchanged when analyzer finds nothing
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

