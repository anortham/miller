# Dead-code candidates dogfood — evidence gate (Task 5)

**Date:** 2026-07-07 · **Status:** ❌ **GATE FAILED — do not surface `references candidates`** ·
**Feature under test:** `miller references candidates [--json]` (design
`docs/plans/2026-07-07-dead-code-candidates-design.md` rev 2) ·
**Binary:** `1.4.5+1fe27ca2f833` (Release) · **Extractor:** `julie-extract 2.9.0` (schema v4,
`reference_resolution_status=partial`, `reference_resolution_version=1`)

## Verdict

**The evidence gate FAILS.** The design's hard gate is *zero confirmed-live symbols among Miller-repo
candidates*. The Miller repo produced **392 candidates, of which ≈388 (99%) are confirmed live** — real,
referenced production symbols — and only **≈4 are plausibly dead**. Precision ≈ **1%**. This is not a
short, high-signal list; it is dominated by false positives.

The failure is **not** a bug in the Task 1–3 implementation. The code faithfully implements the design.
The design's **rule 2 (name-based liveness)** rests on an assumption about julie-extract's `identifiers`
table that does not hold for C# (or Rust): **the extractor keys a reference identifier by the *terminal*
token of the reference, not by the referenced symbol's own name.** So a symbol used only as
`TypeName.Member(...)`, as a bare `const` inside an expression/collection, or as an extension-method
receiver has **zero** `identifiers` rows carrying its name and is falsely flagged. See
[Root cause](#root-cause) for the decisive evidence.

Per the Task 5 brief this is a **stop-and-report gate**, not a fix lane. The findings below are recorded;
no "success" commit is made; the false positives are reported to the lead as a design/data mismatch. The
lead owns any corrective dispatch against Core/Indexing/CLI. Task 5 edits no code.

---

## Headline numbers

| Repo | Examined | Candidates | Confirmed live (false positive) | Plausibly dead (correct) | Precision | CLI wall-clock (compact / --json) |
|------|---------:|-----------:|--------------------------------:|-------------------------:|----------:|-----------------------------------|
| **miller** (`/Users/murphy/source/miller`) | 9,186 | **392** | **≈388** | ≈4 | **≈1.0%** | **4.80 s** / 3.71 s |
| **julie-extractors** (`/Users/murphy/source/julie-extractors`) | 11,267 | **653** | **≈627+** | ≈0 (26 "maybe-dead" are QML runtime handlers + 2 test helpers) | **≈0%** | **5.64 s** / 5.77 s |

CLI performance is **acceptable** and does **not** fail the gate: the reader's 4 indexed subqueries per
candidate-kind symbol complete in ~5 s on the real ~38.9k-symbol Miller artifact (38,927 symbols total;
9,186 of candidate kinds examined) and the ~122k-symbol julie-extractors artifact. Wall-clock is a
report-only metric; it is fine. `files_skipped_stale=0` in both runs (fresh scans, no stale content
hashes).

---

## Reproduction recipe (live-artifact-safe)

Both live repos and the user-scope registry were left untouched. Everything ran against throwaway
`git clone --local` copies scanned into `<copy>/.miller/symbols.db`, with the copy as cwd so
`WorkspaceContext` resolves `<cwd>/.miller/symbols.db` — no registry entry, no live-state contact.

```
scratch=$(mktemp -d)                       # /private/tmp/dogfood-dead-code.d7Yk9N (deleted at end)
git clone --local /Users/murphy/source/miller           $scratch/miller
git clone --local /Users/murphy/source/julie-extractors $scratch/julie-extractors
# per repo:
mkdir -p $scratch/<name>/.miller
/Users/murphy/source/miller/.tools/julie-extract scan \
    --root $scratch/<name> --db $scratch/<name>/.miller/symbols.db
cd $scratch/<name> && \
    /Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller \
    references candidates [--json] [--limit N]
rm -rf $scratch                            # cleanup
```

Extractor scan times (context only, not the metric under test): miller ≈ 4 m 08 s; julie-extractors ≈ 6 m 60 s.
Artifact metadata (both): `schema_version=4`, `binary_version=2.9.0`, `hash_algorithm=blake3`,
`reference_resolution_status=partial`, `reference_resolution_version=1`.

**Hand-verification method.** Miller's own `search`/`inspect` MCP tools were not used (stale sidecar).
Every Miller-repo candidate was cross-referenced against the clone's source with a word-boundary,
binary-safe `rg -w -a` sweep across all code files, excluding only the candidate's own definition line and
excluding non-code hits (`*.md`, `docs/`, `.memories/`). A candidate with any code reference outside its
definition is **confirmed live**. The 6 that survived that automated sweep were then inspected by hand
(read source + targeted `grep -a`) and classified individually. This IS the gate's hand-verification: it
reads real references, not Miller's index.

---

## Miller repo — full results

Compact header:

```
candidates: 392 of 9186 symbols examined · resolver: partial — candidates are facts to check, not deletions to make.
```

Suppression counts (all nine rule ids):

```
suppressed: public_api=3357 visibility_unknown=709 test_symbol=0 entry_point=3 framework_bound=0
            annotated=6 generated_path=0 low_evidence_language=0 string_literal_match=88
literal_scan: files_scanned=479 files_skipped_stale=0
```

Per-language coverage (resolved identifiers / identifiers, computed at query time):

```
bash: 5.5% — name-evidence only;   csharp: 16.0% resolved;   css: 0.0% — name-evidence only;
html: 0.0% — name-evidence only;   javascript: 10.1% resolved;   json: 0.0% — name-evidence only;
markdown: 0.0% — name-evidence only;   powershell: 4.2% — name-evidence only;   python: 14.1% resolved;
razor: 2.1% — name-evidence only;   yaml: 0.0% — name-evidence only
```

Candidate composition: by kind — constant 288, method 38, class 38, property 26, enum 1, function 1. By
language — csharp 368, razor 23, bash 1. By evidence label — `name+resolver` 368, `name` 24. By visibility
— private 390, protected 2. **Every candidate has evidence counts `[name_matches=0 resolved_in=0
pending_in=0 calls_in=0]`** — that is definitional (rules 2 + 3 require all four to be zero), so the
per-candidate counts add no information; the `example reference` column in Appendix A is the real signal.

### Precision summary (hand-verified)

| Verdict | Count | Meaning |
|---------|------:|---------|
| confirmed-live — cross-file reference | **101** | Symbol name used in code in a *different* file. Unambiguously alive. |
| confirmed-live — same-file reference | **285** | Symbol name used elsewhere in its own file (other class member / method body). Alive. |
| plausibly dead / correct find | **4** | Genuinely unreferenced private/internal member — the tool's true positives. |
| syntax/runtime-invoked (live but name never recurs) | **2** | `IFS` (bash builtin), `TelemetryOutcomeExtensions` (extension-method container). |
| **Total** | **392** | |

Confirmed-live: 101 + 285 + 2 = **388**. Gate requires 0. **FAIL.**

### The 6 candidates that survived the automated sweep (hand-inspected)

| Candidate | path:line | Real status | Evidence |
|-----------|-----------|-------------|----------|
| `IFS` (constant, bash) | `scripts/release-promote.sh:167` | **live (false positive)** | Bash special variable, `while IFS= read -r -d ''`. Extractor captured a shell builtin as a `constant` symbol; it actively controls `read`. |
| `TelemetryOutcomeExtensions` (class) | `src/Miller.Server/Telemetry/TelemetryOutcome.cs:20` | **live (false positive)** | Extension-method container. Its method `ToStorageString` is called at `TelemetryScope.cs:274`, `TelemetryCallToolFilter.cs:249,256`. The class name never appears at call sites — a structural blind spot for name-based liveness. |
| `RegionBackendMetadata` (internal const) | `src/Miller.Server/Tools/SearchTool.cs:431` | **plausibly dead (correct)** | `internal const string`; the only repo-wide occurrence is its declaration. Declared alongside `DiskBackendMetadata`/`MemoryBackendMetadata` but never wired into `SearchBackendMetadata`. |
| `TextContentBackendMetadata` (internal const) | `src/Miller.Server/Tools/SearchTool.cs:434` | **plausibly dead (correct)** | Same as above — declared, never referenced. |
| `SearchBackendMetadata` (private method) | `src/Miller.Server/Tools/SearchTool.cs:436` | **plausibly dead (correct)** | `private static string SearchBackendMetadata(...)`; no call site in code (one mention in a `.memories/*.md` note only). Unused private method. |
| `UnknownWorkspaceIdNote` (private method) | `src/Miller.Server/Tools/WorkspaceTool.cs:968` | **plausibly dead (correct)** | `private static string`; only occurrence repo-wide is its declaration. Unused private method. |

> **Tooling caveat, not a product finding:** the file `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
> contains 2 embedded NUL bytes, so `file`/`grep`/`rg` classify it as *binary* and skip it by default. An
> initial (non-`-a`) sweep therefore mis-listed its 6 route-table constants (`ConventionalActions`,
> `CollectionRoutes`, `SingularRoutes`, `LaravelResourceRoutes`, `LaravelApiResourceRoutes`,
> `PhoenixResourceRoutes`) as "maybe-dead". The binary-safe (`rg -a`) re-sweep confirmed all six are used
> in-file (e.g. `CollectionRoutes` at line 608, `ConventionalActions` at 815/822) — **live**. The final
> numbers above use the binary-safe sweep. (The NUL bytes are a pre-existing quirk of the Miller source,
> unrelated to this feature.)

### Representative confirmed-live false positives (deep-dived)

All of these are `private`/`protected`, `evidence=name+resolver`, and were flagged with `name_matches=0`
because their name never appears in the `identifiers` table:

| Candidate | Flagged at | Actually referenced at | Reference form |
|-----------|-----------|------------------------|----------------|
| `GraphTraversal` (class) | `Graph/GraphTraversal.cs:3` | `SqliteSymbolGraphIndex.cs:40,43`, `SymbolGraph.cs:171,187` | `GraphTraversal.Reach(...)` — static-class access (6 refs) |
| `StructuralRouteFactAdapter` (class) | `Graph/StructuralRouteFactAdapter.cs:6` | `DotnetWebBridgeProvider.cs:255,256,297,…` (45 refs) | `StructuralRouteFactAdapter.MetadataString(...)` — static access |
| `VisibilityUnknown` (const) | `DeadCode/DeadCodeCandidates.cs:34` | `DeadCodeCandidates.cs:59,205` | bare `const` inside a collection expression / return |
| `IsCSharpUserType` (method) | `Graph/DotnetWebBridgeProvider.cs:716` | `DotnetWebBridgeProvider.cs:66` | `.Where(IsCSharpUserType)` — method-group argument |
| `AspNetMinimalApiRoutePattern` (const) | `Graph/DotnetWebBridgeProvider.cs:14` | `DotnetWebBridgeProvider.cs:252` | `string.Equals(fact.PatternId, AspNetMinimalApiRoutePattern, …)` |
| `ContextLines` (const) | `Editing/UnifiedDiff.cs:15` | `UnifiedDiff.cs:153,169` (+ doc-comment refs) | arithmetic use inside methods |

---

## Root cause

The `identifiers` table on the live v4 Miller artifact holds **94,700 rows across only three kinds** —
`call` (52,495), `member_access` (26,253), `type_usage` (15,952) — spanning **5,373 distinct names**. A
reference is stored keyed by the **terminal token**, not by the referenced symbol's declared name:

```
-- GraphTraversal.Reach(...)  →  identifier name = 'Reach', kind = 'call'
sqlite> SELECT name, kind, start_line FROM identifiers WHERE name='Reach' LIMIT 3;
Reach|call|171
Reach|call|40
Reach|call|330
-- The receiver/type name is NOT stored in identifiers.name:
sqlite> SELECT COUNT(*) FROM identifiers WHERE name='GraphTraversal';       -- 0
sqlite> SELECT COUNT(*) FROM identifiers WHERE name='VisibilityUnknown';    -- 0
sqlite> SELECT COUNT(*) FROM identifiers WHERE name='StructuralRouteFactAdapter'; -- 0
sqlite> SELECT COUNT(*) FROM identifiers WHERE name='IsCSharpUserType';     -- 0
```

Design rule 2 declares a symbol name-dead when *"no row in `identifiers` has `name = S.name` outside S's
own definition."* But for the reference forms that dominate C# — and Rust (`Type::method()`,
`path::to::CONST`) — the referenced symbol's own name is **never** the identifier's `name`:

- **Static-class / type access** (`X.Foo()`, `X.Bar`, `X::y`) → identifier `name='Foo'/'Bar'/'y'`, never `X`.
  Kills every helper class used statically (`GraphTraversal`, `StructuralRouteFactAdapter`,
  `JulieSchemaGate`, …) and every route-pattern/config `const` compared by value.
- **Bare `const`/field in an expression or collection literal** (`[ …, VisibilityUnknown, … ]`) →
  frequently **no** identifier row at all.
- **Extension-method receiver** (`x.ToStorageString()`) → the container class name never recurs.

The v4 resolver overlay cannot rescue these: measured resolution coverage is **15–16% for C#** (design's
own load-bearing measurement), so `identifier_resolutions` / `pending_resolutions` are silent for ~85% of
real edges. Rule 3 (resolved/pending/calls inbound) is correctly one-directional — it can only *save* a
symbol — so it does not compensate for rule 2's blind spot; it just leaves the false positive standing.

**Net:** rule 2 is not a name-liveness test on this data; it is a *"is this symbol the terminal token of
some reference"* test, which almost no type, static helper, `const`, or extension class satisfies. The
design's expectation — *"non-public C# symbols with zero name matches are rare in a heavily-tested
codebase"* — is contradicted by the artifact: they are the common case.

---

## julie-extractors repo — results (summarized)

Compact header:

```
candidates: 653 of 11267 symbols examined · resolver: partial — candidates are facts to check, not deletions to make.
```

```
suppressed: public_api=343 visibility_unknown=701 test_symbol=3157 entry_point=0 framework_bound=52
            annotated=4 generated_path=0 low_evidence_language=0 string_literal_match=51
literal_scan: files_scanned=1222 files_skipped_stale=0
```

Coverage spans ~37 languages (bash 10.5%, c 57.1%, cpp 70.0%, csharp 9.3% name-only, go 30.8%, java 20.5%,
python 19.2%, rust 16.0%, swift 41.2%, vbnet 71.4%, zig 69.2%, … css/html/json/sql/toml/yaml 0.0%
name-only). Candidate composition: rust 621, qml 32; by kind constant 459, function 174, property 20.
Here `test_symbol` (3,157) did the heavy suppression lifting rather than `public_api`.

Automated cross-reference sweep of all 653 candidates: **194 cross-file live, 433 same-file live, 26
"maybe-dead"**. The 26 (Appendix B) are **not** genuine dead code either: 24 are QML declarative signal
handlers / `Action` properties (`onDrop`, `onXChanged`, `onDragMove`, `quitAction`, …) invoked by the QML
runtime by binding, and 2 are Rust test debug helpers in `src/tests/r/`. So julie-extractors is **~653
false positives, ~0 true dead code** — the same failure mode, driven by Rust's `Type::method` / path-const
reference forms.

---

## `visibility_unknown` honesty rule

The design forecast `visibility_unknown` would dominate. On these two artifacts it did **not** — it fired
709 (miller) / 701 (julie-extractors) times, well behind `public_api` (3,357, miller) and `test_symbol`
(3,157, julie-extractors). It is doing useful work but is not the load-bearing suppressor. That is a minor
observation; it does not change the verdict. The dominant problem is upstream of suppression: rule 2 admits
hundreds of live symbols as candidates before any suppression rule runs.

---

## Recommendation to the lead (design/data mismatch — not an implementation bug)

The gate blocks surfacing `references candidates` in `miller report`, the dashboard, README, or agent
guidance — as designed. Options for the corrective dispatch (all touch Core/Indexing, which Task 5 does not
own):

1. **Strengthen name-liveness to match the extractor's identifier model.** Rule 2 must treat a symbol as
   alive when its name appears as the *receiver/qualifier* of a `member_access`/`call`/`type_usage`
   identifier, not only as the identifier's terminal `name`. The receiver text is available in
   `identifiers.code_context` (and `metadata_json`); alternatively julie-extractors could emit a
   receiver/qualifier column. Until name-liveness sees `X` in `X.Foo()`, the rule cannot work for C#/Rust.
2. **Require resolved-edge coverage the design does not yet have.** At 15–16% C# resolution the resolver
   cannot carry liveness; candidacy on `name+resolver` languages is still ~99% false. Gating candidacy on
   *presence of a resolvable reference model per language* (not just ≥10% for the evidence *label*) would
   shrink the surface honestly.
3. **Keep the feature CLI-only and undocumented** (design's stated fallback for a noisy list) until (1) or
   (2) lands and this gate is re-run green.

This is a plan/design mismatch: the shipped code matches the plan; the plan's rule 2 does not match
julie-extract 2.9.0's `identifiers` semantics. Re-run this gate after a fix.

---

## Appendix A — full Miller candidate list (392, hand-verified)

Sorted by path then line. `verdict` is the hand-verification result; `example reference` is one real code
reference outside the definition (for live rows). All rows have `[name_matches=0 resolved_in=0 pending_in=0
calls_in=0]` by construction (omitted). "see manual notes" rows are detailed in
[the 6-candidate table above](#the-6-candidates-that-survived-the-automated-sweep-hand-inspected).

| # | name | kind | lang | evidence | path:line | verdict | example reference |
|--:|------|------|------|----------|-----------|---------|-------------------|
| 1 | `IFS` | constant | bash | name | `scripts/release-promote.sh:167` | **see manual notes** | — |
| 2 | `CamelRx` | constant | csharp | name+resolver | `spike/Codesearch.Spike/CodeTokenizer.cs:78` | confirmed-live (same-file) | `spike/Codesearch.Spike/CodeTokenizer.cs:89` |
| 3 | `VisibilityUnknown` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:34` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:59` |
| 4 | `EntryPoint` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:36` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:59` |
| 5 | `FrameworkBound` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:37` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:59` |
| 6 | `Annotated` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:38` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:60` |
| 7 | `GeneratedPath` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:39` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:60` |
| 8 | `LowEvidenceLanguage` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:40` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:60` |
| 9 | `StringLiteralMatch` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:41` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:60` |
| 10 | `ExportedVisibilities` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:64` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:201` |
| 11 | `GeneratedSegments` | constant | csharp | name+resolver | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:68` | confirmed-live (same-file) | `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:246` |
| 12 | `ContextLines` | constant | csharp | name+resolver | `src/Miller.Core/Editing/UnifiedDiff.cs:15` | confirmed-live (same-file) | `src/Miller.Core/Editing/UnifiedDiff.cs:10` |
| 13 | `ConventionalActions` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:470` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:815` |
| 14 | `CollectionRoutes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:476` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:608` |
| 15 | `SingularRoutes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:489` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:608` |
| 16 | `LaravelResourceRoutes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:503` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:637` |
| 17 | `LaravelApiResourceRoutes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:517` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:638` |
| 18 | `PhoenixResourceRoutes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:530` | confirmed-live (same-file) | `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs:686` |
| 19 | `NoEdges` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BridgeGraph.cs:74` | confirmed-live (same-file) | `src/Miller.Core/Graph/BridgeGraph.cs:262` |
| 20 | `NoProviders` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BridgeGraph.cs:75` | confirmed-live (same-file) | `src/Miller.Core/Graph/BridgeGraph.cs:116` |
| 21 | `NoProvenance` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BridgeGraph.cs:76` | confirmed-live (same-file) | `src/Miller.Core/Graph/BridgeGraph.cs:186` |
| 22 | `DefaultProviders` | constant | csharp | name+resolver | `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23` | confirmed-live (cross-file) | `src/Miller.Indexing/BridgeProviderSelection.cs:8` |
| 23 | `AspNetMinimalApiRoutePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:14` | confirmed-live (same-file) | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:252` |
| 24 | `AspNetAttributeRoutePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:15` | confirmed-live (same-file) | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:294` |
| 25 | `IsCSharpUserType` | method | csharp | name+resolver | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:716` | confirmed-live (same-file) | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:66` |
| 26 | `HttpVerbKeys` | constant | csharp | name+resolver | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:729` | confirmed-live (same-file) | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:736` |
| 27 | `BodyBearingVerbKeys` | constant | csharp | name+resolver | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:744` | confirmed-live (same-file) | `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:748` |
| 28 | `GraphTraversal` | class | csharp | name+resolver | `src/Miller.Core/Graph/GraphTraversal.cs:3` | confirmed-live (cross-file) | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:40` |
| 29 | `EmptyObservationNodes` | constant | csharp | name+resolver | `src/Miller.Core/Graph/IBridgeProvider.cs:47` | confirmed-live (same-file) | `src/Miller.Core/Graph/IBridgeProvider.cs:40` |
| 30 | `VueRouteDefinitionPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:10` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:279` |
| 31 | `ReactRouteReferencePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:11` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:274` |
| 32 | `ReactRouteDefinitionPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:12` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:280` |
| 33 | `NextJsRouteReferencePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:13` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:275` |
| 34 | `NextJsFileRoutePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:14` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:281` |
| 35 | `NuxtRouteReferencePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:15` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:276` |
| 36 | `NuxtFileRoutePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:16` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:282` |
| 37 | `HttpClientRequestPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:17` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:106` |
| 38 | `NextJsRouteHandlerPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:18` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:285` |
| 39 | `NuxtServerRoutePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:19` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:286` |
| 40 | `SpringRequestMappingPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:20` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:195` |
| 41 | `ExpressRouterMountPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:21` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:294` |
| 42 | `FastApiIncludeRouterPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:22` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:295` |
| 43 | `FlaskBlueprintRegistrationPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:23` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:296` |
| 44 | `DjangoUrlIncludePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:24` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:297` |
| 45 | `AxumNestPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:26` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:301` |
| 46 | `ActixMountPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:27` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:302` |
| 47 | `PhoenixForwardPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:28` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:303` |
| 48 | `LaravelRoutePrefixPattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:29` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:304` |
| 49 | `StructuralRouteFactAdapter` | class | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:6` | confirmed-live (cross-file) | `src/Miller.Core/Resolver/RouteBridge.cs:231` |
| 50 | `HtmxAttributePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:8` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:44` |
| 51 | `VueRouteReferencePattern` | constant | csharp | name+resolver | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:9` | confirmed-live (same-file) | `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs:273` |
| 52 | `HighBase` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:56` | confirmed-live (same-file) | `src/Miller.Core/Resolver/BridgeScorer.cs:165` |
| 53 | `HighCeiling` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:57` | confirmed-live (same-file) | `src/Miller.Core/Resolver/BridgeScorer.cs:165` |
| 54 | `MediumBase` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:58` | confirmed-live (same-file) | `src/Miller.Core/Resolver/BridgeScorer.cs:166` |
| 55 | `MediumCeiling` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:59` | confirmed-live (same-file) | `src/Miller.Core/Resolver/BridgeScorer.cs:166` |
| 56 | `CorroboratorStep` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:60` | confirmed-live (same-file) | `src/Miller.Core/Resolver/BridgeScorer.cs:169` |
| 57 | `MinAnchoringFieldCount` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/BridgeScorer.cs:64` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:1181` |
| 58 | `Wrappers` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/FieldSetExtractor.cs:22` | confirmed-live (same-file) | `src/Miller.Core/Resolver/FieldSetExtractor.cs:36` |
| 59 | `ParamModifiers` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/FieldSetExtractor.cs:249` | confirmed-live (same-file) | `src/Miller.Core/Resolver/FieldSetExtractor.cs:258` |
| 60 | `MemberModifiers` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/FieldSetExtractor.cs:252` | confirmed-live (same-file) | `src/Miller.Core/Resolver/FieldSetExtractor.cs:170` |
| 61 | `BareNonTypes` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/FieldSetExtractor.cs:37` | confirmed-live (same-file) | `src/Miller.Core/Resolver/FieldSetExtractor.cs:103` |
| 62 | `FileRouteBridge` | class | csharp | name+resolver | `src/Miller.Core/Resolver/FileRouteBridge.cs:8` | confirmed-live (cross-file) | `src/Miller.Core/Graph/FileRouteBridgeProvider.cs:69` |
| 63 | `Suffixes` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/NameNormalizer.cs:18` | confirmed-live (same-file) | `src/Miller.Core/Resolver/NameNormalizer.cs:59` |
| 64 | `HttpVerbs` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/RouteNormalizer.cs:31` | confirmed-live (same-file) | `src/Miller.Core/Resolver/RouteNormalizer.cs:119` |
| 65 | `ParamPattern` | constant | csharp | name+resolver | `src/Miller.Core/Resolver/RouteNormalizer.cs:41` | confirmed-live (same-file) | `src/Miller.Core/Resolver/RouteNormalizer.cs:186` |
| 66 | `TokenPhraseBoost` | constant | csharp | name+resolver | `src/Miller.Core/Search/ContentSearchIndex.cs:21` | confirmed-live (cross-file) | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:12` |
| 67 | `WindowRadius` | constant | csharp | name+resolver | `src/Miller.Core/Search/ContentSearchIndex.cs:24` | confirmed-live (same-file) | `src/Miller.Core/Search/ContentSearchIndex.cs:110` |
| 68 | `CoverageStopWords` | constant | csharp | name+resolver | `src/Miller.Core/Search/TextSearchQueryPlan.cs:8` | confirmed-live (same-file) | `src/Miller.Core/Search/TextSearchQueryPlan.cs:72` |
| 69 | `MachineWide` | property | razor | name | `src/Miller.Dashboard/Components/ActivityFeedPanel.razor:86` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/ActivityFeedPanel.razor:33` |
| 70 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/ActivityFeedPanel.razor:88` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 71 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:75` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 72 | `MaxSavedBytes` | property | razor | name | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:79` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:82` |
| 73 | `ToolWidth` | method | razor | name | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:81` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:56` |
| 74 | `EffectiveActivity` | property | razor | name | `src/Miller.Dashboard/Components/DashboardContent.razor:11` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspacesShell.razor:46` |
| 75 | `HealthStateClass` | method | razor | name | `src/Miller.Dashboard/Components/PatternInventoryPanel.razor:51` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:11` |
| 76 | `AllWorkspaces` | property | razor | name | `src/Miller.Dashboard/Components/TelemetryPanel.razor:144` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/TelemetryPanel.razor:27` |
| 77 | `WindowLabel` | property | razor | name | `src/Miller.Dashboard/Components/TelemetryPanel.razor:146` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/TelemetryPanel.razor:12` |
| 78 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:211` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 79 | `MaxLanguageFiles` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:215` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:221` |
| 80 | `MaxSymbolKindCount` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:217` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:224` |
| 81 | `LanguageWidth` | method | razor | name | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:220` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:165` |
| 82 | `KindWidth` | method | razor | name | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:223` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor:191` |
| 83 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:103` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/ContextSavingsPanel.razor:7` |
| 84 | `HealthStateClass` | method | razor | name | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:107` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/PatternInventoryPanel.razor:11` |
| 85 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceLocalMetricsPanel.razor:90` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 86 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceOnboardingPanel.razor:132` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 87 | `EffectiveActivity` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceShell.razor:58` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/DashboardContent.razor:4` |
| 88 | `Heading` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceShell.razor:61` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceShell.razor:14` |
| 89 | `Subtitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceShell.razor:66` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor:7` |
| 90 | `PageTitle` | property | razor | name | `src/Miller.Dashboard/Components/WorkspaceShell.razor:71` | confirmed-live (same-file) | `src/Miller.Dashboard/Components/WorkspaceShell.razor:6` |
| 91 | `EffectiveActivity` | property | razor | name | `src/Miller.Dashboard/Components/WorkspacesShell.razor:70` | confirmed-live (cross-file) | `src/Miller.Dashboard/Components/DashboardContent.razor:4` |
| 92 | `JsonContext` | constant | csharp | name+resolver | `src/Miller.Dashboard/DashboardData.cs:329` | confirmed-live (same-file) | `src/Miller.Dashboard/DashboardData.cs:365` |
| 93 | `DefaultTtl` | constant | csharp | name+resolver | `src/Miller.Dashboard/DashboardIndexFactsCache.cs:15` | confirmed-live (same-file) | `src/Miller.Dashboard/DashboardIndexFactsCache.cs:23` |
| 94 | `ContentSidecar` | constant | csharp | name+resolver | `src/Miller.Dashboard/DashboardIndexFactsReader.cs:9` | confirmed-live (same-file) | `src/Miller.Dashboard/DashboardIndexFactsReader.cs:269` |
| 95 | `BridgeProviderSelection` | class | csharp | name+resolver | `src/Miller.Indexing/BridgeProviderSelection.cs:6` | confirmed-live (cross-file) | `src/Miller.Indexing/RepositoryIndexLoader.cs:79` |
| 96 | `DefaultProviders` | constant | csharp | name+resolver | `src/Miller.Indexing/BridgeProviderSelection.cs:8` | confirmed-live (cross-file) | `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23` |
| 97 | `StrictUtf8` | constant | csharp | name+resolver | `src/Miller.Indexing/ContentCorpusExternalStore.cs:15` | confirmed-live (cross-file) | `src/Miller.Indexing/SourceTextDecoder.cs:7` |
| 98 | `MaxWorkspaceFileBytes` | constant | csharp | name+resolver | `src/Miller.Indexing/ContentCorpusWriter.cs:10` | confirmed-live (same-file) | `src/Miller.Indexing/ContentCorpusWriter.cs:263` |
| 99 | `ProseExtensions` | constant | csharp | name+resolver | `src/Miller.Indexing/ContentFileClassifier.cs:11` | confirmed-live (same-file) | `src/Miller.Indexing/ContentFileClassifier.cs:43` |
| 100 | `ConfigExtensions` | constant | csharp | name+resolver | `src/Miller.Indexing/ContentFileClassifier.cs:17` | confirmed-live (same-file) | `src/Miller.Indexing/ContentFileClassifier.cs:43` |
| 101 | `ContentFileClassifier` | class | csharp | name+resolver | `src/Miller.Indexing/ContentFileClassifier.cs:9` | confirmed-live (cross-file) | `src/Miller.Indexing/ContentCorpusWriter.cs:261` |
| 102 | `MaxContentBytes` | constant | csharp | name+resolver | `src/Miller.Indexing/ContentSearchProjectionLoader.cs:16` | confirmed-live (same-file) | `src/Miller.Indexing/ContentSearchProjectionLoader.cs:54` |
| 103 | `RequiredResolutionTables` | constant | csharp | name+resolver | `src/Miller.Indexing/DeadCodeCandidateReader.cs:40` | confirmed-live (same-file) | `src/Miller.Indexing/DeadCodeCandidateReader.cs:80` |
| 104 | `ExtractVersionMismatch` | class | csharp | name+resolver | `src/Miller.Indexing/ExtractVersionMismatch.cs:12` | confirmed-live (cross-file) | `src/Miller.Indexing/JulieExtractVersionProbe.cs:7` |
| 105 | `FilePathSymbolLookup` | class | csharp | name+resolver | `src/Miller.Indexing/FilePathSymbolLookup.cs:3` | confirmed-live (cross-file) | `src/Miller.Indexing/MillerRepositoryIndex.cs:255` |
| 106 | `SnippetMaxChars` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsRegionSearchIndex.cs:13` | confirmed-live (same-file) | `src/Miller.Indexing/FtsRegionSearchIndex.cs:442` |
| 107 | `TestSegments` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsRegionSearchIndex.cs:15` | confirmed-live (cross-file) | `src/Miller.Server/Resolution/IsTestPath.cs:26` |
| 108 | `FileNameInfixes` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsRegionSearchIndex.cs:20` | confirmed-live (cross-file) | `src/Miller.Server/Resolution/IsTestPath.cs:32` |
| 109 | `PascalSuffixes` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsRegionSearchIndex.cs:22` | confirmed-live (cross-file) | `src/Miller.Server/Resolution/IsTestPath.cs:36` |
| 110 | `TrigramWindow` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsSymbolSearchIndex.cs:271` | confirmed-live (same-file) | `src/Miller.Indexing/FtsSymbolSearchIndex.cs:249` |
| 111 | `LoadPaths` | method | csharp | name+resolver | `src/Miller.Indexing/FtsSymbolSearchIndex.cs:516` | confirmed-live (same-file) | `src/Miller.Indexing/FtsSymbolSearchIndex.cs:34` |
| 112 | `SnippetRadius` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:10` | confirmed-live (same-file) | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:427` |
| 113 | `WidenedCandidateLimit` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:11` | confirmed-live (same-file) | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:159` |
| 114 | `TokenPhraseBoost` | constant | csharp | name+resolver | `src/Miller.Indexing/FtsTextContentSearchIndex.cs:12` | confirmed-live (cross-file) | `src/Miller.Core/Search/ContentSearchIndex.cs:21` |
| 115 | `DefaultInitialDelay` | constant | csharp | name+resolver | `src/Miller.Indexing/FullRebuildPromotion.cs:15` | confirmed-live (same-file) | `src/Miller.Indexing/FullRebuildPromotion.cs:26` |
| 116 | `DefaultMaxDelay` | constant | csharp | name+resolver | `src/Miller.Indexing/FullRebuildPromotion.cs:16` | confirmed-live (same-file) | `src/Miller.Indexing/FullRebuildPromotion.cs:27` |
| 117 | `Semver` | constant | csharp | name+resolver | `src/Miller.Indexing/JulieExtractVersionProbe.cs:17` | confirmed-live (cross-file) | `src/Miller.Indexing/LeadershipEligibility.cs:29` |
| 118 | `MaxEnumeratedFiles` | constant | csharp | name+resolver | `src/Miller.Indexing/JulieIgnoreSeeder.cs:24` | confirmed-live (same-file) | `src/Miller.Indexing/JulieIgnoreSeeder.cs:110` |
| 119 | `WalkSkipDirectories` | constant | csharp | name+resolver | `src/Miller.Indexing/JulieIgnoreSeeder.cs:29` | confirmed-live (same-file) | `src/Miller.Indexing/JulieIgnoreSeeder.cs:137` |
| 120 | `JulieSchemaGate` | class | csharp | name+resolver | `src/Miller.Indexing/JulieSchemaGate.cs:14` | confirmed-live (cross-file) | `src/Miller.Indexing/CloneGroupReader.cs:24` |
| 121 | `SqliteGenericError` | constant | csharp | name+resolver | `src/Miller.Indexing/JulieSchemaGate.cs:17` | confirmed-live (same-file) | `src/Miller.Indexing/JulieSchemaGate.cs:111` |
| 122 | `Semver` | constant | csharp | name+resolver | `src/Miller.Indexing/LeadershipEligibility.cs:31` | confirmed-live (cross-file) | `src/Miller.Indexing/JulieExtractVersionProbe.cs:17` |
| 123 | `SeparatorChars` | constant | csharp | name+resolver | `src/Miller.Indexing/MillerRepositoryIndex.cs:222` | confirmed-live (cross-file) | `src/Miller.Indexing/WorkspaceIndexFactsReader.cs:70` |
| 124 | `EmptyBridgeGraph` | constant | csharp | name+resolver | `src/Miller.Indexing/MillerRepositoryIndex.cs:44` | confirmed-live (same-file) | `src/Miller.Indexing/MillerRepositoryIndex.cs:111` |
| 125 | `PatternDirectory` | class | csharp | name+resolver | `src/Miller.Indexing/PatternFactsReader.cs:637` | confirmed-live (same-file) | `src/Miller.Indexing/PatternFactsReader.cs:358` |
| 126 | `PatternMetadataSql` | class | csharp | name+resolver | `src/Miller.Indexing/PatternMetadataSql.cs:11` | confirmed-live (cross-file) | `src/Miller.Indexing/PatternFactsReader.cs:238` |
| 127 | `PatternPathGlobMatcher` | class | csharp | name+resolver | `src/Miller.Indexing/PatternPathGlobMatcher.cs:9` | confirmed-live (cross-file) | `src/Miller.Indexing/PatternFactsReader.cs:260` |
| 128 | `PatternPathGlobSql` | class | csharp | name+resolver | `src/Miller.Indexing/PatternPathGlobSql.cs:10` | confirmed-live (cross-file) | `src/Miller.Indexing/PatternFactsReader.cs:235` |
| 129 | `MaxIdentifierFallbackTargets` | constant | csharp | name+resolver | `src/Miller.Indexing/RepositoryIndexLoader.cs:30` | confirmed-live (same-file) | `src/Miller.Indexing/RepositoryIndexLoader.cs:72` |
| 130 | `ParameterChunkSize` | constant | csharp | name+resolver | `src/Miller.Indexing/SearchIndexWriter.cs:30` | confirmed-live (cross-file) | `src/Miller.Indexing/SqliteSymbolReader.cs:21` |
| 131 | `StrictUtf16Le` | constant | csharp | name+resolver | `src/Miller.Indexing/SourceTextDecoder.cs:10` | confirmed-live (same-file) | `src/Miller.Indexing/SourceTextDecoder.cs:35` |
| 132 | `StrictUtf16Be` | constant | csharp | name+resolver | `src/Miller.Indexing/SourceTextDecoder.cs:13` | confirmed-live (same-file) | `src/Miller.Indexing/SourceTextDecoder.cs:37` |
| 133 | `SourceTextDecoder` | class | csharp | name+resolver | `src/Miller.Indexing/SourceTextDecoder.cs:5` | confirmed-live (cross-file) | `src/Miller.Indexing/ContentCorpusWriter.cs:312` |
| 134 | `StrictUtf8` | constant | csharp | name+resolver | `src/Miller.Indexing/SourceTextDecoder.cs:7` | confirmed-live (cross-file) | `src/Miller.Indexing/ContentCorpusExternalStore.cs:15` |
| 135 | `SqliteReadOnlyAccess` | class | csharp | name+resolver | `src/Miller.Indexing/SqliteReadOnlyAccess.cs:18` | confirmed-live (cross-file) | `src/Miller.Indexing/ContentCorpusContextReader.cs:25` |
| 136 | `WritableDirs` | constant | csharp | name+resolver | `src/Miller.Indexing/SqliteReadOnlyAccess.cs:80` | confirmed-live (same-file) | `src/Miller.Indexing/SqliteReadOnlyAccess.cs:87` |
| 137 | `DefaultMaxNameResolutionTargets` | constant | csharp | name+resolver | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:12` | confirmed-live (same-file) | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:26` |
| 138 | `Connection` | property | csharp | name+resolver | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:159` | confirmed-live (same-file) | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:76` |
| 139 | `ParameterChunkSize` | constant | csharp | name+resolver | `src/Miller.Indexing/SqliteSymbolReader.cs:21` | confirmed-live (cross-file) | `src/Miller.Indexing/SearchIndexWriter.cs:30` |
| 140 | `ExportJson` | class | csharp | name+resolver | `src/Miller.Indexing/SymbolExportReader.cs:88` | confirmed-live (cross-file) | `src/Miller.Indexing/ReferenceExportReader.cs:90` |
| 141 | `SeparatorChars` | constant | csharp | name+resolver | `src/Miller.Indexing/SymbolLookupTables.cs:15` | confirmed-live (cross-file) | `src/Miller.Indexing/MillerRepositoryIndex.cs:218` |
| 142 | `VendorDirectoryNames` | constant | csharp | name+resolver | `src/Miller.Indexing/VendorScan.cs:26` | confirmed-live (same-file) | `src/Miller.Indexing/VendorScan.cs:93` |
| 143 | `ReadFileStatuses` | method | csharp | name+resolver | `src/Miller.Indexing/WorkspaceHealthReader.cs:180` | confirmed-live (same-file) | `src/Miller.Indexing/WorkspaceHealthReader.cs:26` |
| 144 | `ReadComplexityMetrics` | method | csharp | name+resolver | `src/Miller.Indexing/WorkspaceHealthReader.cs:225` | confirmed-live (same-file) | `src/Miller.Indexing/WorkspaceHealthReader.cs:25` |
| 145 | `ReadParseDiagnostics` | method | csharp | name+resolver | `src/Miller.Indexing/WorkspaceHealthReader.cs:49` | confirmed-live (same-file) | `src/Miller.Indexing/WorkspaceHealthReader.cs:21` |
| 146 | `ReadCapabilityGaps` | method | csharp | name+resolver | `src/Miller.Indexing/WorkspaceHealthReader.cs:71` | confirmed-live (same-file) | `src/Miller.Indexing/WorkspaceHealthReader.cs:22` |
| 147 | `ReadLanguageCapabilities` | method | csharp | name+resolver | `src/Miller.Indexing/WorkspaceHealthReader.cs:94` | confirmed-live (same-file) | `src/Miller.Indexing/WorkspaceHealthReader.cs:23` |
| 148 | `SeparatorChars` | constant | csharp | name+resolver | `src/Miller.Indexing/WorkspaceIndexFactsReader.cs:74` | confirmed-live (cross-file) | `src/Miller.Indexing/MillerRepositoryIndex.cs:218` |
| 149 | `CreateTableDdl` | constant | csharp | name+resolver | `src/Miller.Indexing/WorkspaceRegistry.cs:8` | confirmed-live (cross-file) | `src/Miller.Server/Telemetry/TelemetryLedger.cs:18` |
| 150 | `WorkspaceRelativePath` | class | csharp | name+resolver | `src/Miller.Indexing/WorkspaceRelativePath.cs:11` | confirmed-live (cross-file) | `src/Miller.Indexing/ContentCorpusWriter.cs:269` |
| 151 | `CliCapabilities` | class | csharp | name+resolver | `src/Miller.Server/Cli/CliCapabilities.cs:10` | confirmed-live (cross-file) | `src/Miller.Server/Cli/CliDispatch.cs:228` |
| 152 | `JsonCommands` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliCapabilities.cs:41` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliCapabilities.cs:141` |
| 153 | `JsonContracts` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliCapabilities.cs:90` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliCapabilities.cs:144` |
| 154 | `ImpactUsage` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:1070` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:1025` |
| 155 | `ImpactDeltaUsage` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:1075` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:1089` |
| 156 | `HelpText` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:2178` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:94` |
| 157 | `WorkspaceHelpText` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:2239` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:1196` |
| 158 | `DeadCodeCandidatesDefaultLimit` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:755` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:747` |
| 159 | `DeadCodeCandidatesSchemaVersion` | constant | csharp | name+resolver | `src/Miller.Server/Cli/CliDispatch.cs:899` | confirmed-live (same-file) | `src/Miller.Server/Cli/CliDispatch.cs:823` |
| 160 | `IsHealthy` | method | csharp | name+resolver | `src/Miller.Server/Cli/DashboardCliLauncher.cs:257` | confirmed-live (same-file) | `src/Miller.Server/Cli/DashboardCliLauncher.cs:48` |
| 161 | `TryAcquireLaunchLock` | method | csharp | name+resolver | `src/Miller.Server/Cli/DashboardCliLauncher.cs:278` | confirmed-live (same-file) | `src/Miller.Server/Cli/DashboardCliLauncher.cs:49` |
| 162 | `WriteMetadata` | method | csharp | name+resolver | `src/Miller.Server/Cli/DashboardCliLauncher.cs:338` | confirmed-live (same-file) | `src/Miller.Server/Cli/DashboardCliLauncher.cs:50` |
| 163 | `HealthBody` | constant | csharp | name+resolver | `src/Miller.Server/Cli/DashboardCliLauncher.cs:37` | confirmed-live (same-file) | `src/Miller.Server/Cli/DashboardCliLauncher.cs:270` |
| 164 | `UnixLaunchScript` | constant | csharp | name+resolver | `src/Miller.Server/Cli/DashboardCliLauncher.cs:441` | confirmed-live (same-file) | `src/Miller.Server/Cli/DashboardCliLauncher.cs:421` |
| 165 | `UnitSeparator` | constant | csharp | name+resolver | `src/Miller.Server/Git/GitHistoryReader.cs:25` | confirmed-live (same-file) | `src/Miller.Server/Git/GitHistoryReader.cs:42` |
| 166 | `AtomicTempMove` | method | csharp | name+resolver | `src/Miller.Server/Hosting/EditApplier.cs:140` | confirmed-live (same-file) | `src/Miller.Server/Hosting/EditApplier.cs:60` |
| 167 | `TempSuffix` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/EditApplier.cs:32` | confirmed-live (same-file) | `src/Miller.Server/Hosting/EditApplier.cs:151` |
| 168 | `ExecuteAsync` | method | csharp | name+resolver | `src/Miller.Server/Hosting/FreshnessService.cs:68` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/MillerServiceRegistration.cs:22` |
| 169 | `BindOutcome` | enum | csharp | name+resolver | `src/Miller.Server/Hosting/IndexBootstrapService.cs:30` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/WorkspaceToolTests.cs:383` |
| 170 | `CurrentBound` | property | csharp | name+resolver | `src/Miller.Server/Hosting/IndexBootstrapService.cs:93` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexBootstrapService.cs:100` |
| 171 | `KeepPriorCodes` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerCore.cs:139` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerCore.cs:179` |
| 172 | `OnRenamed` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:1026` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:961` |
| 173 | `OnError` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:1056` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:962` |
| 174 | `OnHeadChanged` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:1063` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:965` |
| 175 | `OnIgnorePolicyChanged` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:1069` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:966` |
| 176 | `ExecuteAsync` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:207` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/MillerServiceRegistration.cs:22` |
| 177 | `DefaultLeaderRetryInterval` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:35` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:98` |
| 178 | `ProbeBundledExtractorVersion` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:471` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:166` |
| 179 | `OnChanged` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:969` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:960` |
| 180 | `OnDirectoryChanged` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerService.cs:995` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerService.cs:963` |
| 181 | `OnHeadRenamed` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerWatcherSet.cs:145` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerWatcherSet.cs:78` |
| 182 | `OnIgnorePolicyRenamed` | method | csharp | name+resolver | `src/Miller.Server/Hosting/IndexerWatcherSet.cs:148` | confirmed-live (same-file) | `src/Miller.Server/Hosting/IndexerWatcherSet.cs:95` |
| 183 | `ProbeProcess` | method | csharp | name+resolver | `src/Miller.Server/Hosting/LeaderIdentityFile.cs:140` | confirmed-live (same-file) | `src/Miller.Server/Hosting/LeaderIdentityFile.cs:118` |
| 184 | `PidReuseStartTolerance` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/LeaderIdentityFile.cs:85` | confirmed-live (same-file) | `src/Miller.Server/Hosting/LeaderIdentityFile.cs:122` |
| 185 | `SidecarCorruptionRecovery` | class | csharp | name+resolver | `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs:7` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/IndexerSidecarConverger.cs:38` |
| 186 | `SegmentComparer` | property | csharp | name+resolver | `src/Miller.Server/Hosting/WatchPathFilter.cs:125` | confirmed-live (same-file) | `src/Miller.Server/Hosting/WatchPathFilter.cs:32` |
| 187 | `SkipSegments` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/WatchPathFilter.cs:32` | confirmed-live (same-file) | `src/Miller.Server/Hosting/WatchPathFilter.cs:82` |
| 188 | `IgnorePolicyFiles` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/WatchPathFilter.cs:48` | confirmed-live (same-file) | `src/Miller.Server/Hosting/WatchPathFilter.cs:102` |
| 189 | `PathComparison` | property | csharp | name+resolver | `src/Miller.Server/Hosting/WorkspaceBindingResolver.cs:171` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:83` |
| 190 | `WorkspaceIgnorePolicy` | class | csharp | name+resolver | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:11` | confirmed-live (cross-file) | `src/Miller.Indexing/JulieIgnoreSeeder.cs:12` |
| 191 | `SeparatorChars` | constant | csharp | name+resolver | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:13` | confirmed-live (cross-file) | `src/Miller.Indexing/MillerRepositoryIndex.cs:218` |
| 192 | `PathComparison` | property | csharp | name+resolver | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:213` | confirmed-live (cross-file) | `src/Miller.Server/Tools/WorkspaceRootSafety.cs:78` |
| 193 | `DirectoryOnly` | property | csharp | name+resolver | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:241` | confirmed-live (same-file) | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:226` |
| 194 | `Ellipsis` | constant | csharp | name+resolver | `src/Miller.Server/Logging/ExtractErrorLog.cs:32` | confirmed-live (same-file) | `src/Miller.Server/Logging/ExtractErrorLog.cs:67` |
| 195 | `HumanOutputTemplate` | constant | csharp | name+resolver | `src/Miller.Server/Logging/MillerLoggingSetup.cs:45` | confirmed-live (same-file) | `src/Miller.Server/Logging/MillerLoggingSetup.cs:76` |
| 196 | `TestSegments` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/IsTestPath.cs:26` | confirmed-live (cross-file) | `src/Miller.Indexing/FtsRegionSearchIndex.cs:15` |
| 197 | `FileNameInfixes` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/IsTestPath.cs:32` | confirmed-live (cross-file) | `src/Miller.Indexing/FtsRegionSearchIndex.cs:20` |
| 198 | `PascalSuffixes` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/IsTestPath.cs:36` | confirmed-live (cross-file) | `src/Miller.Indexing/FtsRegionSearchIndex.cs:22` |
| 199 | `MaxSuggestions` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/SmartTargetResolver.cs:142` | confirmed-live (same-file) | `src/Miller.Server/Resolution/SmartTargetResolver.cs:145` |
| 200 | `IsPreferredSourceSegment` | method | csharp | name+resolver | `src/Miller.Server/Resolution/SmartTargetResolver.cs:197` | confirmed-live (same-file) | `src/Miller.Server/Resolution/SmartTargetResolver.cs:182` |
| 201 | `IsAuxiliaryCodeSegment` | method | csharp | name+resolver | `src/Miller.Server/Resolution/SmartTargetResolver.cs:202` | confirmed-live (same-file) | `src/Miller.Server/Resolution/SmartTargetResolver.cs:184` |
| 202 | `SymbolSuggestionEngine` | class | csharp | name+resolver | `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs:6` | confirmed-live (cross-file) | `src/Miller.Server/Resolution/SmartTargetResolver.cs:236` |
| 203 | `SearchCandidateMultiplier` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs:8` | confirmed-live (same-file) | `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs:26` |
| 204 | `MaxEditDistance` | constant | csharp | name+resolver | `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs:9` | confirmed-live (same-file) | `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs:86` |
| 205 | `MissingParameterMarker` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:150` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:178` |
| 206 | `ToolUsageExamples` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:156` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:192` |
| 207 | `BudgetLoggerCategory` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:34` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs:214` |
| 208 | `SelectSql` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryExportReader.cs:48` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryExportReader.cs:35` |
| 209 | `CreateTableDdl` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryLedger.cs:18` | confirmed-live (cross-file) | `src/Miller.Indexing/WorkspaceRegistry.cs:8` |
| 210 | `TelemetryOutcomeExtensions` | class | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryOutcome.cs:20` | **see manual notes** | — |
| 211 | `MaxErrorTextChars` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryScope.cs:17` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryScope.cs:253` |
| 212 | `CurrentScope` | constant | csharp | name+resolver | `src/Miller.Server/Telemetry/TelemetryScope.cs:298` | confirmed-live (same-file) | `src/Miller.Server/Telemetry/TelemetryScope.cs:303` |
| 213 | `CandidateOutput` | class | csharp | name+resolver | `src/Miller.Server/Tools/CandidateOutput.cs:6` | confirmed-live (cross-file) | `src/Miller.Server/Tools/ImpactTool.cs:775` |
| 214 | `SignatureMaxLength` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:145` | confirmed-live (cross-file) | `src/Miller.Server/Tools/InspectTool.cs:116` |
| 215 | `SearchSeedLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:146` | confirmed-live (same-file) | `src/Miller.Server/Tools/ContextTool.cs:328` |
| 216 | `ReachCap` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:149` | confirmed-live (same-file) | `src/Miller.Server/Tools/ContextTool.cs:358` |
| 217 | `PathSeparators` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:406` | confirmed-live (same-file) | `src/Miller.Server/Tools/ContextTool.cs:456` |
| 218 | `MaxNeighbourCandidates` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:623` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:147` |
| 219 | `NextInspectCount` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ContextTool.cs:624` | confirmed-live (same-file) | `src/Miller.Server/Tools/ContextTool.cs:683` |
| 220 | `ReplaceTextPlanResult` | class | csharp | name+resolver | `src/Miller.Server/Tools/EditService.cs:1116` | confirmed-live (same-file) | `src/Miller.Server/Tools/EditService.cs:282` |
| 221 | `CompactLikelyTestsLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/ImpactTool.cs:39` | confirmed-live (same-file) | `src/Miller.Server/Tools/ImpactTool.cs:694` |
| 222 | `SignatureMaxLength` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:116` | confirmed-live (cross-file) | `src/Miller.Server/Tools/ContextTool.cs:145` |
| 223 | `RefLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:117` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:409` |
| 224 | `OverviewRelationLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:118` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:409` |
| 225 | `OverviewChildLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:119` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:415` |
| 226 | `OverviewBodyPreviewMaxLines` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:120` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:660` |
| 227 | `OverviewBodyPreviewMaxChars` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:121` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:663` |
| 228 | `ImpactHintMinReferences` | constant | csharp | name+resolver | `src/Miller.Server/Tools/InspectTool.cs:125` | confirmed-live (same-file) | `src/Miller.Server/Tools/InspectTool.cs:479` |
| 229 | `MarkerSearch` | class | csharp | name+resolver | `src/Miller.Server/Tools/MarkerSearch.cs:10` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/MarkerSearchTests.cs:19` |
| 230 | `DefaultMarkers` | constant | csharp | name+resolver | `src/Miller.Server/Tools/MarkerSearch.cs:14` | confirmed-live (same-file) | `src/Miller.Server/Tools/MarkerSearch.cs:15` |
| 231 | `AllowedMarkers` | constant | csharp | name+resolver | `src/Miller.Server/Tools/MarkerSearch.cs:15` | confirmed-live (same-file) | `src/Miller.Server/Tools/MarkerSearch.cs:123` |
| 232 | `CommentKinds` | constant | csharp | name+resolver | `src/Miller.Server/Tools/MarkerSearch.cs:16` | confirmed-live (same-file) | `src/Miller.Server/Tools/MarkerSearch.cs:63` |
| 233 | `NextStepHint` | class | csharp | name+resolver | `src/Miller.Server/Tools/NextStepHint.cs:13` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/NextStepHintTests.cs:7` |
| 234 | `ParseSingleWhere` | method | csharp | name+resolver | `src/Miller.Server/Tools/PatternsTool.cs:189` | confirmed-live (same-file) | `src/Miller.Server/Tools/PatternsTool.cs:185` |
| 235 | `MetadataPriority` | constant | csharp | name+resolver | `src/Miller.Server/Tools/PatternsTool.cs:19` | confirmed-live (same-file) | `src/Miller.Server/Tools/PatternsTool.cs:917` |
| 236 | `ReadToolWorkspaceRouting` | class | csharp | name+resolver | `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs:7` | confirmed-live (cross-file) | `src/Miller.Server/Tools/PatternsTool.cs:74` |
| 237 | `DiskBackendMetadata` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:425` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:427` |
| 238 | `MemoryBackendMetadata` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:428` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:437` |
| 239 | `RegionBackendMetadata` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:431` | **see manual notes** | — |
| 240 | `TextContentBackendMetadata` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:434` | **see manual notes** | — |
| 241 | `SearchBackendMetadata` | method | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:436` | **see manual notes** | — |
| 242 | `EmptyHintQueryLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:567` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:573` |
| 243 | `SymbolNoResultsHint` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:60` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:1698` |
| 244 | `EmptySuggestionLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:62` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:753` |
| 245 | `OutsideScopeHintLimit` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:620` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:717` |
| 246 | `SignatureMaxLength` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:621` | confirmed-live (cross-file) | `src/Miller.Server/Tools/InspectTool.cs:116` |
| 247 | `RegionsUsageHint` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:63` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:403` |
| 248 | `OverFetchEscalationWindows` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:642` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:655` |
| 249 | `WorkspaceContentSearchKinds` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:66` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:857` |
| 250 | `PathQueryExtensions` | constant | csharp | name+resolver | `src/Miller.Server/Tools/SearchTool.cs:72` | confirmed-live (same-file) | `src/Miller.Server/Tools/SearchTool.cs:1432` |
| 251 | `FileRouteDiagnosticProviders` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:1018` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:835` |
| 252 | `ModeAuto` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:138` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:184` |
| 253 | `ModePath` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:139` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:188` |
| 254 | `ModeRefs` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:140` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:189` |
| 255 | `ModeBridge` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:141` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:190` |
| 256 | `MaxNextActions` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:142` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:1795` |
| 257 | `KnownReferenceKinds` | constant | csharp | name+resolver | `src/Miller.Server/Tools/TraceTool.cs:171` | confirmed-live (same-file) | `src/Miller.Server/Tools/TraceTool.cs:627` |
| 258 | `WorkspaceFactsAssembler` | class | csharp | name+resolver | `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs:15` | confirmed-live (cross-file) | `src/Miller.Server/Cli/CliDispatch.cs:1253` |
| 259 | `WorkspaceOnboardingAssembler` | class | csharp | name+resolver | `src/Miller.Server/Tools/WorkspaceOnboardingAssembler.cs:7` | confirmed-live (cross-file) | `src/Miller.Server/Cli/CliDispatch.cs:1485` |
| 260 | `PathComparison` | property | csharp | name+resolver | `src/Miller.Server/Tools/WorkspaceRootSafety.cs:157` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:83` |
| 261 | `PathComparison` | property | csharp | name+resolver | `src/Miller.Server/Tools/WorkspaceSafety.cs:63` | confirmed-live (cross-file) | `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs:83` |
| 262 | `UnknownWorkspaceIdNote` | method | csharp | name+resolver | `src/Miller.Server/Tools/WorkspaceTool.cs:968` | **see manual notes** | — |
| 263 | `DefaultLockBusyWait` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:11` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:44` |
| 264 | `DefaultFullScanRequestWait` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:12` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:51` |
| 265 | `DefaultLockBusyPollInterval` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:13` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:45` |
| 266 | `IneligibleRemedy` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:32` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:176` |
| 267 | `OperationFullScan` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:72` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:109` |
| 268 | `OperationFileConverge` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:73` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:206` |
| 269 | `OperationYield` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:74` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:312` |
| 270 | `OperationLeaderHandoff` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:75` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:422` |
| 271 | `RequestDirectoryName` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:76` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:590` |
| 272 | `ClaimedSuffix` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:81` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:531` |
| 273 | `StampFormat` | constant | csharp | name+resolver | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:82` | confirmed-live (same-file) | `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs:104` |
| 274 | `WorkspaceFreshnessView` | class | csharp | name+resolver | `src/Miller.Server/Workspaces/WorkspaceFreshnessView.cs:5` | confirmed-live (cross-file) | `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs:209` |
| 275 | `WorkspaceRegistryRootMatcher` | class | csharp | name+resolver | `src/Miller.Server/Workspaces/WorkspaceRegistryRootMatcher.cs:6` | confirmed-live (cross-file) | `src/Miller.Server/Cli/CliDispatch.cs:1891` |
| 276 | `ExemptFileNames` | constant | csharp | name+resolver | `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs:34` | confirmed-live (same-file) | `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs:60` |
| 277 | `BetaCriticalScripts` | constant | csharp | name+resolver | `tests/Miller.Tests/Conventions/ScriptPlatformConventionTests.cs:11` | confirmed-live (same-file) | `tests/Miller.Tests/Conventions/ScriptPlatformConventionTests.cs:25` |
| 278 | `CSharpResolverCovered` | constant | csharp | name+resolver | `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs:62` | confirmed-live (same-file) | `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs:113` |
| 279 | `FileA` | constant | csharp | name+resolver | `tests/Miller.Tests/Editing/RenamePlannerTests.cs:17` | confirmed-live (same-file) | `tests/Miller.Tests/Editing/RenamePlannerTests.cs:31` |
| 280 | `AllExist` | constant | csharp | name+resolver | `tests/Miller.Tests/Freshness/WatchEventRouterTests.cs:18` | confirmed-live (same-file) | `tests/Miller.Tests/Freshness/WatchEventRouterTests.cs:44` |
| 281 | `NoneExist` | constant | csharp | name+resolver | `tests/Miller.Tests/Freshness/WatchEventRouterTests.cs:19` | confirmed-live (same-file) | `tests/Miller.Tests/Freshness/WatchEventRouterTests.cs:58` |
| 282 | `FailedJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:101` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:176` |
| 283 | `ChangedJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:15` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:81` |
| 284 | `NoChangeJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:32` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:131` |
| 285 | `DeletedJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:48` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:141` |
| 286 | `NotFoundJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:64` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:151` |
| 287 | `PartialJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:81` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:161` |
| 288 | `NoSymbols` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/FreshnessReaderTests.cs:19` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/FreshnessReaderTests.cs:25` |
| 289 | `SqliteReadOnlyAccessTestSeam` | class | csharp | name+resolver | `tests/Miller.Tests/Indexing/FullRebuildScanScaleTests.cs:108` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/FullRebuildScanScaleTests.cs:91` |
| 290 | `FilesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1065` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:456` |
| 291 | `SymbolsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1080` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:457` |
| 292 | `IdentifiersDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1106` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:458` |
| 293 | `RelationshipsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1124` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:460` |
| 294 | `SourceRegionsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1139` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:461` |
| 295 | `SourceRegionsIndexesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1157` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:462` |
| 296 | `PatternCatalogDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1163` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:464` |
| 297 | `StructuralFactsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1173` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:463` |
| 298 | `ComplexityMetricsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1194` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:465` |
| 299 | `TypeArgumentUsagesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1224` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:471` |
| 300 | `TypeArgumentsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1235` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:472` |
| 301 | `LiteralsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1245` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:473` |
| 302 | `SymbolAnnotationsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1267` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:474` |
| 303 | `ParserInventoryDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1281` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:476` |
| 304 | `ParseDiagnosticsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1293` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:477` |
| 305 | `LanguageCapabilitiesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1311` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:478` |
| 306 | `LanguageCapabilityFixturesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1331` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:479` |
| 307 | `LanguageCapabilityGapsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1341` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:480` |
| 308 | `PendingRelationshipsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1356` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:481` |
| 309 | `PendingRelationshipsIndexesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1383` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:482` |
| 310 | `IdentifierResolutionsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1392` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:485` |
| 311 | `IdentifierResolutionsIndexDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1406` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:486` |
| 312 | `PendingResolutionsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1411` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:487` |
| 313 | `PendingResolutionsIndexDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1423` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:488` |
| 314 | `TypeFactsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1426` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:489` |
| 315 | `MetadataDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1439` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:490` |
| 316 | `ExtractionRevisionsDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1450` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:467` |
| 317 | `RevisionFileChangesDdl` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:1466` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieDbFixture.cs:468` |
| 318 | `ScanSuccessJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:115` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:135` |
| 319 | `InfoJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:146` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:165` |
| 320 | `AbsDb` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:16` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:16` |
| 321 | `AbsRoot` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:17` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:17` |
| 322 | `FailedJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:176` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:101` |
| 323 | `AbsFile` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:18` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:18` |
| 324 | `LanguagesJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:70` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:83` |
| 325 | `FailedUpdateJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:106` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:125` |
| 326 | `AbsDb` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:16` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:16` |
| 327 | `AbsRoot` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:17` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:17` |
| 328 | `AbsFile` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:18` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs:18` |
| 329 | `ChangedJson` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs:81` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs:15` |
| 330 | `NoRows` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:21` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:41` |
| 331 | `PinSchema` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:23` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:41` |
| 332 | `PinContract` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:24` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:25` |
| 333 | `PinContractStr` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:25` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs:41` |
| 334 | `RequiredClientLanguages` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:1121` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:1093` |
| 335 | `TableDisplays` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:2385` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:2377` |
| 336 | `RouteDisplays` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:2387` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:2379` |
| 337 | `ValidateId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/MillerRepositoryIndexTests.cs:127` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/RepositoryIndexLoaderTests.cs:16` |
| 338 | `HandleId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/MillerRepositoryIndexTests.cs:128` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:28` |
| 339 | `ValidateId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/RepositoryIndexLoaderTests.cs:16` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/MillerRepositoryIndexTests.cs:127` |
| 340 | `DefaultArtifactId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/RevisionDeltaReaderTests.cs:16` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs:19` |
| 341 | `SqliteFixtureMutator` | class | csharp | name+resolver | `tests/Miller.Tests/Indexing/SqliteFixtureMutator.cs:5` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/WorkspaceToolTests.cs:568` |
| 342 | `ValidateId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:27` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/RepositoryIndexLoaderTests.cs:16` |
| 343 | `HandleId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:28` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:32` |
| 344 | `LogAId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:30` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:44` |
| 345 | `LogBId` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:31` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:44` |
| 346 | `NameMap` | constant | csharp | name+resolver | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:38` | confirmed-live (same-file) | `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs:48` |
| 347 | `ReadToolRoutingTestSupport` | class | csharp | name+resolver | `tests/Miller.Tests/ReadToolRoutingTestSupport.cs:165` | confirmed-live (cross-file) | `tests/Miller.Tests/Tools/TraceToolTests.cs:319` |
| 348 | `SearchEvalQueries` | class | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:227` | confirmed-live (cross-file) | `tests/Miller.Tests/Search/SearchRecallEvalTests.cs:37` |
| 349 | `MinComponentLength` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:229` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:317` |
| 350 | `InteriorMin` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:230` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:276` |
| 351 | `InteriorMax` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:231` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:283` |
| 352 | `RecallMetrics` | class | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:337` | confirmed-live (cross-file) | `tests/Miller.Tests/Search/SearchRecallEvalTests.cs:98` |
| 353 | `RecallK` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:63` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:124` |
| 354 | `FetchLimit` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:64` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:120` |
| 355 | `PopularityOf` | function | csharp | name+resolver | `tests/Miller.Tests/Search/SearchRecallEval.cs:89` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SearchRecallEval.cs:112` |
| 356 | `ArtifactRevision` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SymbolSearchEvalScaleTests.cs:29` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SymbolSearchEvalScaleTests.cs:74` |
| 357 | `InteriorRecallFloor_x100` | constant | csharp | name+resolver | `tests/Miller.Tests/Search/SymbolSearchEvalScaleTests.cs:30` | confirmed-live (same-file) | `tests/Miller.Tests/Search/SymbolSearchEvalScaleTests.cs:129` |
| 358 | `DefaultToolDescriptionChars` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:31` | confirmed-live (same-file) | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:40` |
| 359 | `ToolDescriptionBudgets` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:32` | confirmed-live (same-file) | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:40` |
| 360 | `ToolsThatMustRedirectInNotForClause` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:50` | confirmed-live (same-file) | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:203` |
| 361 | `AllToolNames` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:62` | confirmed-live (same-file) | `tests/Miller.Tests/Server/AgentInstructionsTests.cs:206` |
| 362 | `NullLoggerFactory` | class | csharp | name+resolver | `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs:476` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/IndexerWatcherExtensionGateTests.cs:91` |
| 363 | `DefaultArtifactId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs:19` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/RevisionDeltaReaderTests.cs:16` |
| 364 | `ControllerId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ContextToolTests.cs:23` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:38` |
| 365 | `ServiceId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ContextToolTests.cs:24` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:40` |
| 366 | `RepoId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ContextToolTests.cs:25` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:42` |
| 367 | `UnrelatedId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ContextToolTests.cs:26` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:44` |
| 368 | `TestId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ContextToolTests.cs:27` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ContextToolTests.cs:46` |
| 369 | `LogLoggerGate` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/CorrelationFilterTests.cs:49` | confirmed-live (same-file) | `tests/Miller.Tests/Server/CorrelationFilterTests.cs:93` |
| 370 | `EditFixtureFiles` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/EditToolTests.cs:46` | confirmed-live (same-file) | `tests/Miller.Tests/Server/EditToolTests.cs:170` |
| 371 | `ValidateId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:30` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/RepositoryIndexLoaderTests.cs:16` |
| 372 | `HandleId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:32` | confirmed-live (cross-file) | `tests/Miller.Tests/Indexing/MillerRepositoryIndexTests.cs:128` |
| 373 | `ProcessWorksId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:33` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:52` |
| 374 | `LonelyId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:34` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:54` |
| 375 | `HelperId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:35` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:93` |
| 376 | `ImportId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:36` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:94` |
| 377 | `ModuleId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:37` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:95` |
| 378 | `RustParseConfigId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:829` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:839` |
| 379 | `RustTestId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:830` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:841` |
| 380 | `RustHelperId` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/ImpactToolTests.cs:831` | confirmed-live (same-file) | `tests/Miller.Tests/Server/ImpactToolTests.cs:844` |
| 381 | `BeginScope` | method | csharp | name+resolver | `tests/Miller.Tests/Server/IndexerServiceLeadershipTests.cs:91` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/WorkspaceRegistryScanPublisherTests.cs:102` |
| 382 | `BeginScope` | method | csharp | name+resolver | `tests/Miller.Tests/Server/IndexerServiceScanTests.cs:116` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/SoftBudgetFilterTests.cs:47` |
| 383 | `ThrowIfSearchBuiltCalled` | method | csharp | name+resolver | `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs:164` | confirmed-live (same-file) | `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs:46` |
| 384 | `ThrowIfSearchCurrentCalled` | method | csharp | name+resolver | `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs:174` | confirmed-live (same-file) | `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs:25` |
| 385 | `CsOnly` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/IndexerWatcherExtensionGateTests.cs:19` | confirmed-live (same-file) | `tests/Miller.Tests/Server/IndexerWatcherExtensionGateTests.cs:103` |
| 386 | `DocCommentBodyContent` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/InspectToolTests.cs:413` | confirmed-live (same-file) | `tests/Miller.Tests/Server/InspectToolTests.cs:438` |
| 387 | `LargeDbWriter` | class | csharp | name+resolver | `tests/Miller.Tests/Server/LargeDbWriter.cs:20` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/RebuildLatencyTests.cs:90` |
| 388 | `ScopedEnvironment` | class | csharp | name+resolver | `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs:447` | confirmed-live (same-file) | `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs:120` |
| 389 | `BeginScope` | method | csharp | name+resolver | `tests/Miller.Tests/Server/WorkspaceRegistryScanPublisherTests.cs:102` | confirmed-live (cross-file) | `tests/Miller.Tests/Server/SoftBudgetFilterTests.cs:47` |
| 390 | `Forbidden` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/WorkspaceRootSafetyTests.cs:17` | confirmed-live (same-file) | `tests/Miller.Tests/Server/WorkspaceRootSafetyTests.cs:30` |
| 391 | `OtherWs` | constant | csharp | name+resolver | `tests/Miller.Tests/Server/WorkspaceToolTests.cs:32` | confirmed-live (same-file) | `tests/Miller.Tests/Server/WorkspaceToolTests.cs:430` |
| 392 | `JsonOptions` | constant | csharp | name+resolver | `tools/Miller.SearchQuality/SearchQuality.cs:460` | confirmed-live (same-file) | `tools/Miller.SearchQuality/SearchQuality.cs:497` |

## Appendix B — julie-extractors "maybe-dead" candidates (26 of 653)

The other 627 candidates are confirmed live by cross-reference (194 cross-file, 433 same-file).
These 26 survived the automated sweep; all are QML runtime-invoked handlers/actions or Rust test
debug helpers — live, not dead. No genuine dead code was found in julie-extractors.

| name | kind | lang | path:line |
|------|------|------|-----------|
| `debug_r_ast` | function | rust | `crates/julie-extractors/src/tests/r/basics.rs:13` |
| `debug_data_structures_ast` | function | rust | `crates/julie-extractors/src/tests/r/data_structures.rs:13` |
| `onXChanged` | function | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:33` |
| `onYChanged` | function | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:34` |
| `onHeightChanged` | function | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:36` |
| `onFullscreenChanged` | function | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:54` |
| `globalMenuLoader` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:65` |
| `showMenubarAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:77` |
| `fullscreenAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:86` |
| `quitAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:95` |
| `showsettingsAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:101` |
| `copyAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:110` |
| `pasteAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:115` |
| `zoomIn` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:120` |
| `zoomOut` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:126` |
| `showAboutAction` | property | qml | `fixtures/qml/real-world/cool-retro-term-main.qml:132` |
| `onExternalData` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:134` |
| `highlightItemSvg` | property | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:153` |
| `listItemSvg` | property | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:162` |
| `toolBoxSvg` | property | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:171` |
| `onDragEnter` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:208` |
| `onDragMove` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:225` |
| `onDragLeave` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:243` |
| `onDrop` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:254` |
| `onAppletChanged` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:329` |
| `onUserDrag` | function | qml | `fixtures/qml/real-world/kde-plasma-desktop-main.qml:353` |
