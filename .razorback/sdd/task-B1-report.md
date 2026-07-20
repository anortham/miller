# Task B1 report — MILLER_SEMANTIC activation, VectorSidecar skeleton, off-guarantee

**Status:** COMPLETE. Commit SHA: none - parallel-lead-commit.

---

## Implementation

### `src/Miller.Indexing/SemanticActivation.cs` (new)

- `enum SemanticMode { Off, Shadow, On }` — the vectors-v1 three-state activation.
- `SemanticActivation.EnvVar = "MILLER_SEMANTIC"`, `FromEnvironment()`, and the pure
  `FromEnvValue(string?)` mapping (testable without mutating the process environment, mirroring
  `SymbolSearchSidecar.FromEnvValue`, which exists precisely because env mutation leaks across xUnit's
  parallel collections).
- Mapping: `shadow` ⇒ Shadow, `on` ⇒ On, **everything else** (unset, empty, whitespace, `off`, `0`, and any
  unrecognized token) ⇒ Off. Trimmed and case-insensitive.
- Note the deliberate **inversion** relative to `SymbolSearchSidecar`: search-sidecar defaults ON and opts out
  on falsy tokens; semantic defaults OFF and opts *in* on exactly two tokens. Semantic retrieval is opt-in per
  ADR-0003, so an unrecognized value must never silently start doing work the off-guarantee forbids.

### `src/Miller.Indexing/VectorSidecar.cs` (new)

Mirrors `SymbolSearchSidecar` (src/Miller.Indexing/SymbolSearchSidecar.cs:12) in surface:

| SymbolSearchSidecar | VectorSidecar |
|---|---|
| `const string EnvVar` | `const string EnvVar = SemanticActivation.EnvVar` |
| `static Disabled { get; }` | `static Disabled { get; }` (mode Off) |
| `static SearchDbPathFor(symbolsDbPath)` | `static PathFor(workspaceRoot)` → `<root>/.miller/vectors.db` |
| `static FromEnvironment()` | `static FromEnvironment()` |
| `bool Enabled` | `SemanticMode Mode` + `bool Enabled` |
| `SearchSidecarFacts Inspect(...)` | `VectorSidecarFacts Inspect(workspaceRoot)` |
| `TryOpen(...)` non-throwing probe | `bool TryOpen(workspaceRoot, out string? reason)` |
| `OpenRequired(...)` fails visibly | `void OpenRequired(workspaceRoot)` throws `InvalidOperationException` |
| — | `IReadOnlyList<string> RetainedGenerations(workspaceRoot)` |

`EnsureCurrent` is intentionally **not** present — there is no store to converge until B2.

Per the brief, `TryOpen`/`OpenRequired` at this stage validate activation + artifact presence and report the
store as not-yet-available. Both messages name the artifact path and say **``run `miller workspace refresh` ``**,
so an enabled-but-broken sidecar degrades to lexical *with a reason*, never silently.

`VectorSidecarFacts(State, Path, Reason)` is declared at the bottom of the same file, exactly as
`SearchSidecarFacts` is. States emitted by this build: `disabled`, `unavailable`. The remaining vocabulary
(`ready`, `building`, `downloading`, `incompatible`, `circuit-open`, `disk-blocked`) lands in B2–B6.

### Modified: `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`

All three public entry points take a new **optional trailing** `VectorSidecar? vectors = null`, defaulting to
`VectorSidecar.FromEnvironment()`. Every `WorkspaceFacts` construction site (registered / unregistered-local /
missing-index / unreadable-index) now populates `Vectors: <sidecar>.Inspect(<workspace root>)`.

The optional parameter is a deliberate ownership choice: the assembler's callers
(`WorkspaceTool.cs`, `CliDispatch.cs`, `DashboardData.cs`) are **not** in my file ownership, and a required
parameter would have forced edits there. Existing call sites compile untouched. B6 can thread an explicit
instance when it wires real convergence.

### Modified: `src/Miller.Server/Tools/WorkspaceRender.cs` (the render seam)

- `WorkspaceFacts` gains a trailing `VectorSidecarFacts? Vectors = null` (positional record struct; all call
  sites use named arguments, so a trailing optional is source-compatible).
- Compact: a `vectors: …` line beside `search_db:` / `content_db:` in **both** the status and health compact
  renderers, via `VectorsLabel`.
- JSON: `WriteVectorsJson` emits a `vectors` object inside the `index` section of **both** status and health
  JSON, additive per workspace-status-v1 / workspace-health-v1.

---

## How the off-guarantee is enforced and observed

**Enforced** structurally: `VectorSidecar` has exactly one route to the disk — the internal `IVectorFileProbe`
seam (`FileExists`, `EnumerateRetainedGenerations`), with `SystemVectorFileProbe` as the production
implementation. Every public method short-circuits on `!Enabled` **before** reaching the probe:

- `Inspect` returns `new("disabled", PathFor(root), null)` — `PathFor` is pure `Path.Combine` string
  composition, so the `disabled` state is genuinely derived without filesystem access.
- `TryOpen` returns false with a disabled reason.
- `OpenRequired` throws the disabled message.
- `RetainedGenerations` returns `[]` **without enumerating** — vectors-v1 counts the retained-generation probe
  as part of "no `vectors.db` open".

**Observed** three ways in `SemanticOffGuaranteeTests` (chosen over a directory watcher, which is racy and
platform-dependent; the injected probe is the strongest deterministic observable the codebase supports, and
matches the existing constructor-injection style):

1. `OffModes_NeverAskTheFilesystemAnything` — a `RecordingProbe` captures every filesystem question that would
   be asked. Driven through `SemanticActivation.FromEnvValue` for `null`, `"off"`, and `"0"`, exercising
   `Inspect` + `TryOpen` + `RetainedGenerations` + `OpenRequired`, then asserts `probe.Calls` is **empty**.
2. `OffMode_LeavesTheWorkspaceDirectoryByteForByteUnchanged` — a real temp workspace seeded with a sentinel
   `vectors.gen-sentinel.db`, using the **real** `SystemVectorFileProbe`. Asserts the `.miller` entry list is
   identical before/after, no `vectors.db` was created, and the sentinel's bytes are intact.
3. `EnabledMode_DoesAskTheFilesystem_SoTheProbeIsAnHonestObservable` — the negative control. Under `On` the
   probe records calls, proving assertion (1) measures something real rather than a probe that is never wired.

---

## Verification

**Invariants:** (a) under `MILLER_SEMANTIC` off/`0`/unset the sidecar makes zero filesystem calls and reports
`disabled`; (b) existing workspace status/health output is byte-identical when semantic is off; (c) an
enabled-but-unusable sidecar always states a reason and names `miller workspace refresh`.

| Scope | Command | Result |
|---|---|---|
| Worker scope | `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SemanticActivation\|FullyQualifiedName~VectorSidecar\|FullyQualifiedName~SemanticOffGuarantee\|FullyQualifiedName~WorkspaceFactsAssembler"` | **Passed — 46/46, 0 failed, 66 ms** |
| Fast suite (ceiling) | `scripts/test.sh` | **Passed — 3724 passed, 0 failed, 1 skipped** (pre-existing Blazor skip) |
| Build | `dotnet build Miller.slnx -c Release` | **Build succeeded, 0 warnings / 0 errors** |

Timestamp: 2026-07-20.

**Fast-suite wall-clock tripwire fired (58s vs the 30s ceiling) — NOT caused by B1.** Evidence: B1's 36 new
tests run in **389 ms** total. The E1 lane's near-duplicate/MinHash tests alone measure **7 s**
(`--filter "FullyQualifiedName~NearDuplicate|FullyQualifiedName~MinHash"`, 18 tests). The budget is a
shared-suite property and several P2 lanes landed tests concurrently in this worktree; this is a
lead-level reconciliation item, not a B1 defect. Zero test failures in any scope.

---

## Miller calls and what they confirmed

| Call | Confirmed |
|---|---|
| `inspect(target="SymbolSearchSidecar", depth="full")` | Full surface + body: `EnvVar` const at :14, `Disabled` at :18, `SearchDbPathFor` at :39, `FromEnvironment` :52, pure `FromEnvValue` :60/:66, `IsDisabledValue` :84, `Inspect` :97, `TryOpen` :161, `OpenRequired` :193, `EnsureBuilt`/`EnsureCurrent`. Gave the exact structural pattern to mirror and the `OpenRequired` fail-visible message wording. |
| `context(query="WorkspaceFactsAssembler search sidecar facts render into workspace status and health")` | Identified `WorkspaceFactsAssembler` as the seed, `WorkspaceRender` as the render seam, `WorkspaceFactsAssemblerTests` / `WorkspaceRenderTests` as the test seams, and the dashboard razor panels as downstream consumers. |
| `inspect(target="src/Miller.Server/Tools/WorkspaceFactsAssembler.cs")` | All 17 methods + the 4-member `WorkspaceRegisteredFactsProfile` enum; showed the three public entry points and the two private fact builders that each construct a `WorkspaceFacts` — the four sites needing `Vectors`. |

Targeted `grep` was used only after Miller had located the seams, to enumerate the exact emit sites within the
two already-identified files (`SearchSidecarStatus` / `SearchSidecarFacts` / `WriteSearchSidecarJson`).

## API-shape evidence

- `SymbolSearchSidecar.OpenRequired` message wording — read verbatim from the `inspect depth=full` body;
  `VectorSidecar.OpenRequired` reuses the ``Run `miller workspace refresh` `` phrasing.
- `SearchSidecarFacts(State, Path, Revision, ExpectedRevision, DocumentCount, Error)` at
  `SymbolSearchSidecar.cs:417` — the precedent for co-locating the facts record with its sidecar.
- `WorkspaceFacts` positional record struct at `WorkspaceRender.cs:38` with trailing optional parameters
  (`ContentCorpus = null, ArtifactId = null`) — proved a trailing optional is the safe additive shape.
- Four `WorkspaceFacts` construction sites in the assembler (`:33, :73, :154, :187` pre-edit) — all use named
  arguments.
- `WorkspaceRender.Status(WorkspaceFacts, TelemetrySummary, bool json)` at `:218` — the public render entry
  point used by the new tests; `TelemetrySummary.Empty` is the established test fixture
  (`WorkspaceRenderTests.cs:481`).
- `Miller.Indexing.csproj:19` `InternalsVisibleTo Include="Miller.Tests"` — confirmed the internal
  `IVectorFileProbe` seam is injectable from the fast suite without widening its visibility to public.

---

## Self-review

- **Off-guarantee is structural, not conventional.** No `File.`/`Directory.` call exists in `VectorSidecar`
  outside `SystemVectorFileProbe`. A future edit that reaches the disk directly would bypass the recording
  probe, so the negative-control test exists to keep assertion (1) honest.
- **No MCP surface change.** No new tool, no parameter, no `ServerInstructions` text. `AgentInstructionsTests`
  is green in the fast suite.
- **Lexical output untouched.** Nothing in this change reaches the search/ranking path.
- **Comments audited** against the repo comment rule: the four retained inline comments each state a
  non-obvious constraint (why the `vectors:` line is suppressed when off; why the JSON key is omitted; why the
  probe seam exists; why Off defaults on unrecognized tokens). No narration comments; tests carry none.
- **Nullability/analyzers clean** under `TreatWarningsAsErrors`.
- `RetainedGenerations` swallows `IOException`/`UnauthorizedAccessException` and returns empty, matching
  `SymbolSearchSidecar`'s "a sidecar must never break the caller" posture.

## Judgment calls

1. **`disabled` renders nowhere (most consequential).** vectors-v1 §Status vocabulary lists `disabled` in the
   compact line, but p2-global-constraints says *"lane b must not alter any existing tool output when semantic
   is off or absent."* Taken literally these conflict. Resolution: the `disabled` **fact** is always assembled
   and assertable (satisfying acceptance criterion 1), but neither the compact `vectors:` line nor the JSON
   `vectors` key is emitted when the state is `disabled`. So with `MILLER_SEMANTIC` off, status/health output is
   byte-identical to before this change; under shadow/on both surfaces carry it (criterion 2). This is the
   strictly safer and fully reversible reading — **if the lead prefers the literal contract reading, the change
   is a one-line edit to `VectorsLabel` plus the `State == "disabled"` early-return in `WriteVectorsJson`.**
2. **Unrecognized env token ⇒ Off**, inverting `SymbolSearchSidecar`'s default-on convention. Justified above;
   pinned by `FromEnvValue_UnrecognizedToken_FallsBackToOff`.
3. **`PathFor(workspaceRoot)`**, per the brief, rather than `SymbolSearchSidecar`'s `…PathFor(symbolsDbPath)`.
   vectors-v1 specifies placement relative to the workspace root, and every assembler site has a root available
   (`row.CanonicalRoot` / `context.WorkspaceRoot`).
4. **`Inspect` under Off returns a non-null `Path`**, matching `SymbolSearchSidecar`'s disabled facts. It is
   pure string composition, so the "derived without touching the filesystem" clause holds.
5. **Optional `VectorSidecar?` assembler parameter** to respect file ownership (explained above).
6. **No `EnsureCurrent`** — deferred to B2 rather than shipping an empty method.

## Concerns / handoffs

- **Fast-suite 30s tripwire is breached at 58s across the P2 lanes** (B1 contributes 389 ms; E1's
  MinHash/near-duplicate tests alone are 7 s). Needs a lead-level decision — likely re-tagging the heaviest new
  fixtures `[Trait("Category","Scale")]`.
- **The shared worktree is being edited concurrently.** During this task the tree was transiently unbuildable
  from other lanes' in-flight files (`TextReplaceMatcher.cs` / D1, `SearchTool.cs` + `ToolSearchFilters` / C1,
  `MetricsTool.cs` + `NearDuplicate*` / E1). All resolved; the final full build and fast suite are green. No
  contract mismatch found — vectors-v1's stated mirror of `SymbolSearchSidecar` matches code reality exactly.
- **For B2:** `TryOpen`/`OpenRequired` currently return `bool`/`void`. When the real store lands they should
  return the store handle (`TStore?` / `TStore`), matching `SymbolSearchSidecar`'s `FtsSymbolSearchIndex?` /
  `FtsSymbolSearchIndex`. The `out string? reason` parameter should survive that change — it is what makes
  "degrades to lexical **with a reason**" observable. `VectorSidecarFacts` will need the five generation-identity
  fields, cursor lag, and the retained-generation inventory; `RetainedGenerations` and `IVectorFileProbe` are
  already in place for the B5 rollback work.
