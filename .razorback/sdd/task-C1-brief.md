### Task C1: Typed candidate seam in SearchRouteExecutor

**Files:**
- Create: `src/Miller.Core/Search/SymbolCandidate.cs`, `tests/Miller.Tests/Server/SearchGoldenParityTests.cs`
- Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs` (RunSymbols, src/Miller.Server/Tools/SearchRouteExecutor.cs:20), `src/Miller.Server/Tools/SearchTool.cs` (Run seam at :144 caller side)
- Test: `tests/Miller.Tests/Server/SearchRouteExecutorTests.cs` (extend)

**Interfaces:**
- Consumes: `ISymbolLookupIndex`, `SearchRoute`, `SearchRouteExecutionRequest` as they exist today.
- Produces: a typed candidate stage: `IReadOnlyList<SymbolCandidate>` (symbol id, name, path, line, lexical score, enclosing metadata needed by the renderer) flowing candidate-generation → rendering inside RunSymbols, with a single seam method (e.g. `SearchRouteExecutor.CollectSymbolCandidates(...)`) that P3's fusion arm can interpose on. Rendering consumes ONLY the typed list.

**Contract inputs:** Design §6.1. Golden corpus: capture current compact AND json output for a fixed set of ≥12 representative queries (symbol/exact/phrase/filtered/limit-edge/empty-result) against a fixture index BEFORE refactoring; assert byte-identical after.

**File ownership:** Create: `src/Miller.Core/Search/SymbolCandidate.cs`, `tests/Miller.Tests/Server/SearchGoldenParityTests.cs`; Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs`, `src/Miller.Server/Tools/SearchTool.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Split the symbol search route so candidate generation returns typed candidates and rendering is a pure function of them, without changing a single output byte. Only the symbols route needs the seam in P2 (content/text/regions/markers routes are not fusion targets).

**Approach:** Write the golden tests FIRST against current behavior, commit-worthy on their own. Then refactor behind them. `SymbolCandidate` lives in Miller.Core (zero I/O). Keep `SearchRouteExecutionResult` shape unchanged for callers (CliDispatch.cs:320, SearchTool.cs:144).

**Acceptance criteria:**
- [ ] Golden parity tests cover compact + json for ≥12 query shapes and pass byte-identical pre/post refactor
- [ ] RunSymbols internally flows typed candidates; rendering reads only the typed list
- [ ] All existing SearchRouteExecutorTests pass unchanged
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

