# Navigation and editing usefulness implementation plan

**Status — 2026-09-08:** Source implementation and corrective dogfood work are recorded in the [navigation evidence ledger](../findings/2026-09-08-navigation-agent-usefulness-dogfood.md). Producer facts and branch gates are verified in the producer checkout; release and Miller pin adoption remain separate approval boundaries. Source-built facts are not claimed to be installed in the session MCP server. The checklist below preserves the original acceptance criteria; use the ledger for current verified and outstanding status.

> For agentic workers: use `razorback:subagent-driven-development` when delegation is available; otherwise use `razorback:executing-plans`. These are the original planning instructions; implementation proceeded in the later user-directed session. Release remains a separate approval.

**Goal:** Make reference, impact, inspect, and edit workflows complete, bounded, and honest about uncertainty.

**Architecture:** Keep reference truth in the shared evidence reader and query-time resolver, presentation in existing tools, and parsing in julie-extractors. Add members, tests-only, and batch capabilities to existing tools. Preserve pinned snapshots and preview-first edits.

**Tech stack:** .NET 10, C#, SQLite, existing MCP tools; Rust/tree-sitter changes in julie-extractors.

**Architecture quality:** Medium/high risk in evidence propagation, rename coverage, transactional batches, and cache lifetime. Rendering changes have low risk. Never improve apparent recall by declaring uncertain edges exact. Shared immutable cache entries must not capture disposed read sessions.

## Global constraints

- No new MCP tool. Proposed parameters and types below are NEW contracts, not existing APIs.
- Miller.Core keeps zero I/O dependencies. New recognition belongs to julie-extractors across every supported language with applicable syntax.
- Ambiguous overload-family sites remain ambiguous candidates. Exact rename cannot silently consume them.
- `MILLER_SEMANTIC=off` remains a zero-work guarantee. No semantic lookup is added to inspect or edit misses.
- Preserve file freshness checks, byte spans, dry-run behavior, workspace identity checks, write locks, TOCTOU checks, rollback reporting, and write-through convergence.
- Existing compact/JSON contracts need explicit versioning or compatibility tests when changed. Do not globally raise `McpRowLimit` to change inspect alone.
- No production implementation in this validation session. Do not change the source finding in place; its historical measurements remain historical.
- Existing docs and `.memories/` changes belong to the caller. Do not overwrite them.

## Validation ledger

Source reviewed at Miller `40f7c71a7703b369998903e5f9a536fda006da57`, `/home/murphy/source/miller`, branch `main`. It began dirty with `docs/README.md`, `.memories/2026-09-07/`, and the input review. Live calls used registered workspace `miller-91250a0fd4f3`, revisions 87715 through 87727. Producer source inspected read-only at `/home/murphy/source/julie-extractors`, `main`, clean `b7a7c62a061c707dd5809d4c401bf36ed286fbbe`.

“Verified” means source and/or a direct repro supports the defect. “Partial” means the mechanism is supported but a historical number or part of the proposed cause is not. “Rejected” applies to the stated claim or proposed fix, not adjacent verified work.

| Review row | Assessment and evidence | Task |
|---|---|---|
| Impact: tests lost at compact cap | Verified. `ImpactTool.cs:1356-1397` puts tests last; `:1611` cuts at 6,000 chars before the 12 KiB pager sees output. | N1 |
| Impact: homonym edges and hop-2 certainty | Verified. Live `ScanFailureJournal.TryWrite` includes channel-writer users `WorkspaceOpenPrimeService.TryEnqueue` and `StoreViewRetirementDrainService.Enqueue` via `identifier_name`. `GraphTraversal.cs:140-152` copies only the final edge's evidence. Receiver-token string inequality alone is not a safe rejection rule: variables, aliases, inheritance, and dynamic dispatch exist. | N2 |
| Impact: changed tests missing | Verified. `GraphTraversal.cs:85-86` filters hop zero; `ImpactAnalysis.cs:115-118` also puts seeds in the heuristic exclusion set. Changed test seeds cannot return through either path. | N1 |
| Impact: via hash overhead | Verified shape in `ImpactTool.ReachedLine`, `:1436-1440`, and live calls. Historical 50.1% not remeasured. | N1 |
| Impact: JSON fragments and repeated traversal | Verified. `ImpactTool.PageMcpOutput`, `:1470-1508`, pages serialized text using half the 12 KiB budget and runs after execution. The envelope itself is valid JSON; its string fragment can split an object. | N1 |
| Impact: shared limit cuts tests | Verified. `ImpactAnalysis.Compute`, `:61-83`, ranks all candidates and takes the shared limit before partitioning. Tests are not guaranteed always to be cut first, but no quota protects them. | N1 |
| Impact: command and Scale absent | Verified compact renderer has neither. Do not infer runnable case names or Scale from names. Consume attested project/framework metadata from the CT plan. | N1, CT Task 6 |
| Trace: overload yields zero refs | Verified live by exact ID `acbc57729b27012f04e4974a02a461ee`, `FullRebuildPromotion.Promote:113`. `QueryTimeResolver.cs:35-57` requires one candidate; `ReferenceEvidenceReader.cs:98-104` feeds definition count into fallback policy. Reject the review's proposed attribution to every overload as exact. | N2 |
| Trace: nonexistent route claim and bridge cost | Verified false text for `/zzz/nope`. `TraceTool.TryBuildRouteDiagnostic:1555-1580` falls through when neither route matches but other routes exist. Per-call bridge lazy construction at `WorkspaceIndexProvider.cs:278` and `:498` verifies repeated work in store mode; historical timing needs controlled measurement. | N3, N6 |
| Trace: doubled evidence rows | Verified separate exact/fallback populations and site-ID rendering, `TraceTool.cs:1175-1182,1229-1234,1292-1308`; no cross-tier source-span merge in this path. Historical 68/32 counts not remeasured. Do not merge two actual calls on the same line. | N2 |
| Trace: class/file path starts and method groups | Partial. `TraceTool.RunPath:787-820` resolves one symbol, never expands members, and call filter `:876` accepts only calls/call/invokes/instantiates. Treating a method-group value reference as an executed delegate call is rejected without invocation evidence. A final type-usage edge belongs to dependency mode. | N3 |
| Trace: bare method return type missing | Verified with pinned real one-file extraction. `Result Bare()` yielded no return type_usage; `Result? Nullable()` did. Producer `csharp/identifiers.rs:373-412` checks `type`, not `returns`; `:482-489` explicitly excludes bare returns from variable reads. | N9 |
| Trace: Razor interpolated URLs dropped | Partial, cause corrected. Live `WorkspaceTestsPanel` bridge reports no links while 3 hx-post facts retain complete `target_path` with `@Esc(...)`; `StructuralRouteFactAdapter.cs:310` reads it. `RouteNormalizer.cs:41-43` has no Razor expression normalization; relevant backend `MapMethods` coverage is a separate producer gap. Generic attribute extraction is not dropping these values. | N3, N9, retrieval producer task |
| Inspect: 10-row cap | Verified schema `InspectTool.cs:57` already documents default and maximum 10, so “undocumented maximum” is rejected; silent runtime normalization and lack of a larger listing remain. | N4 |
| Inspect: repeated continuation metadata | Verified `RenderSymbolCompact:766-1035` always renders docs/relations before paging body. Live full `RunPath` confirms the repeated sections are large. | N4 |
| Inspect: capped children and ineffective hint | Verified `:839-848` full MCP children use `McpRowLimit`; overview takes first five; real `TraceTool` shows fields/constants then “use depth=full”. | N4 |
| Inspect: ambiguity lacks parents, only 3 retries | Verified `CandidateOutput.cs:21-43` takes 3 examples, then uses IDs for same-file candidates. Live Promote ambiguity confirms. Historical 890 count not remeasured. | N4 |
| Inspect: slow miss, fuzzy/semantic cause | Partial/rejected cause. `SmartTargetResolver.NearMisses:298` delegates to `SymbolSuggestionEngine.Suggest`; `SymbolSuggestionEngine.cs:11-45` uses exact variants and lexical OR, not semantics, and already returns at most 3. Do not add a new three-suggestion feature that already exists. Profile the lookup work before changing it. | N6 |
| Inspect: XML docs and complete values | Verified `InspectTool.cs:791-805` prints raw docs; `:1001-1018` renders body even for a complete constant declaration. | N4 |
| Edit: signature accepted as body | Verified `EditPlanner.ReplaceSymbolBody:34-45` only checks span presence and nonempty text; `EditService.cs:358` passes text verbatim. This is input-shape protection, not general syntax validation. | N5 |
| Edit: full projection before refusal/text edit | Verified `EditTool.cs:148` calls `WorkspaceEditContextFactory.Create`; factory `:87-107` resolves complete symbol read before `EditService.Execute` parses the operation. Historical latency values not remeasured. | N6 |
| Edit: rename homonyms/docs omissions | Partial. `EditService.ExecuteRename:1144-1167` refuses any fallback; `:1175-1187` already excludes sites exactly resolved to other homonyms in include_fallback mode. Unresolved receivers remain risky. No XML/comment/string/Markdown/test-name coverage group in `WriteRenameEvidenceJson:1608-1694`. Historical cref counts not remeasured. | N7 |
| Edit: spanless exact sites | Verified `ExecuteRename:1144-1167` refuses unusable exact spans and tells users to refresh. Recovery may use source bytes only when token identity and location are unique; same-name textual presence is not proof. | N7 |
| Edit: insert_after eats newline | Rejected. `EditPlanner.EnsureLeadingLineBreak:277` preserves leading LF/CRLF; `EditService.cs:361,379-385` passes it unchanged to splicing. Existing `EditPlannerTests.InsertAfter_IsZeroWidthAtEndByte` passed on 2026-09-07, 1/1, explicitly asserting `\n// done`. No behavior change planned. | Preserve in N5 |
| Edit: signature brace gluing, no-match candidate | Verified `ReplaceSymbolSignature:52-70` replaces through body_start with no retained whitespace; `EditService.EditPlanFailureMessage:2387` finds stale exact text only, not nearest current text. | N5 |
| Gap: class members view | Verified inspect supports summary/overview/full only, `:51`; no independent complete member page. | N4 |
| Gap: batch/multi-hunk edits | Verified `EditRequest.cs:12` and `EditTool.Edit:91-111` accept one operation/target. `occurrence=all` only repeats one replacement, not distinct hunks. | N8 |
| Gap: tests-only impact with evidence split | Verified absent from `ImpactTool.Impact` signature and current shared partition. | N1, N2 |
| Gap: homonym-safe rename and not-renamed mentions | Verified missing capabilities, with existing exact-homonym exclusion preserved as above. | N7 |

The pinned probe was created outside registered workspaces at `/tmp/miller-navigation-probe-5b9ak38h`. `julie-extract scan --root <fixture> --db <fixture>/probe.db --jobs 1 --json` exited 0. Its 5-line `Example.cs` contains `class Result {}` and `Factory` methods returning `Result` and `Result?`. The two identifier rows for Result are a constructor call on line 3 and a type_usage on line 4. No repository source changed. This establishes C# failure, not all-language parity.

Repro source and exact query, retained here so the temporary directory is unnecessary:

```csharp
class Result {}
class Factory {
 public Result Bare() => new Result();
 public Result? Nullable() => null;
}
```

```sql
SELECT name, kind, start_line, start_column FROM identifiers WHERE name = 'Result';
```

Observed rows: `Result|call|3|29`, `Result|type_usage|4|8`. Missing expected return row: line 3, column 8.

## Verification strategy

- Source of truth: `CLAUDE.md` Testing/Build and producer repository instructions before upstream work.
- Worker red/green: write the task's behavioral regression first, run `dotnet test --filter "FullyQualifiedName~<listed class>"`, implement, rerun only that scope. Synthetic fixture tests stay fast; any test launching extraction is class-level Scale and uses `ScaleTestSupport.RequireJulieServer()`.
- Worker ceiling: listed classes only. Investigate and repair failures in owned scope; do not weaken guards. Lead owns broader gates.
- Affected-change gate: relevant server, reference, graph and editing classes once after each coherent batch. Track invariant, command, commit/tree, UTC timestamp and result.
- Branch gate: one `dotnet test`, `dotnet build Miller.slnx -c Release` with 0 warnings/errors, and `scripts/test.sh scale` for graph/index/extraction paths. Reuse green evidence on an unchanged tree.
- Security scope: none declared beyond existing edit safety tests; no new dependencies or external services planned.
- Performance hard gates: operation counters prove disk-only preview/invalid operation need zero full projection loads; cache hit must not rebuild; stale key must not reuse data. Wall-time distributions are report-only comparisons on the same artifact, not brittle test assertions.
- Producer gate: real extract per supported language; record `SELECT language, kind, COUNT(*) FROM identifiers GROUP BY 1,2`, expected source spans and applicable/non-applicable cases. Counts alone do not establish coverage. Restore the verified pin before consumer Scale tests. Windows verification and explicit approval are required before a release, which is outside this plan.

## Parallel execution contract

Use `parallel-lead-commit`: workers hand owned changes to the lead for review; they do not commit shared files.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| N2 evidence | A | ReferenceEvidenceReader.cs, GraphTraversal.cs, SqliteSymbolGraphIndex.cs, SymbolGraphReader.cs, evidence/graph tests | No | None; safe parallel batch after initial interface design. |
| N4 inspect | A | InspectTool.cs, CandidateOutput.cs, inspect/candidate tests | No | None; safe parallel batch. |
| N5 edit text correctness | A | EditPlanner.cs, EditService.cs, planner/service tests | No | None; safe parallel batch. |
| N9 producer facts | A then serial | producer csharp/identifiers.rs, markup.rs, corresponding tests; Miller adapter/provider and bridge tests after producer | Yes | Coordinate markup.rs and structural_facts.rs with retrieval producer task; one owner at a time. |
| N1 impact output | B | ImpactTool.cs, ImpactAnalysis.cs, ImpactToolTests.cs, proposed ImpactResultPagingTests.cs | Yes | Consumes N2 path evidence and CT runnable selector; no shared output-budget constant changes. |
| N3 trace | B | TraceTool.cs, TraceToolTests.cs, SymbolGraphShortestPathTests.cs | Yes | Consumes N2 reference projection; N9 route metadata adoption serializes bridge fixtures. |
| N6 read costs | B | WorkspaceEditContextFactory.cs, WorkspaceIndexProvider.cs, SymbolSuggestionEngine.cs, proposed projection cache, provider tests | Yes | Coordinate with retrieval context task and N8 EditTool.cs; one shared cache owner. |
| N7 rename | C | EditService.cs, EditRequest.cs, EditTool.cs, rename/evidence tests | Yes | N2 then N5 complete first; safety relies on evidence classifications. |
| N8 batch edits | D | EditRequest.cs, EditTool.cs, EditService.cs, proposed batch planner, EditApplier.cs and tests | Yes | N5/N6/N7 contracts and changes must land first. |

Exact paths for shortened ownership entries are given in each task below. Discovery may justify splitting a task, but not losing its acceptance criteria.

Cross-plan serialization: N2 shares `QueryTimeResolutionReader.cs` with CT Task 1 and must follow or hand off to its single owner. N6 shares `WorkspaceIndexProvider.cs` with workspace/guidance W1 and runs after that ownership handoff. N9 shares producer `markup.rs` and `structural_facts.rs` with retrieval Task 7; use one producer worker or serialize. N1 consumes CT Task 6.

## N1. Make impact answer the testing question first

Modify `src/Miller.Server/Tools/ImpactTool.cs` (`Impact`, `RenderCompact`, `ReachedLine`, `PageMcpOutput`) and `src/Miller.Indexing/ImpactAnalysis.cs` (`Compute`). Tests: `tests/Miller.Tests/Server/ImpactToolTests.cs`; create `tests/Miller.Tests/Server/ImpactResultPagingTests.cs`.

New proposed inputs on existing impact: `view=all|tests`, `tests_limit`, `symbols_limit`. Keep `limit` behavior for callers that omit new fields. Explicitly document effective limits. Return independent test and symbol populations with total/returned/omitted counts and a cursor bound to workspace, artifact generation, revision, query and ordering. JSON pages contain whole rows, never fragments of the serialized root object. Reserve compact bytes for test rows, diagnostics and continuation before symbols. Do not compute all pages again through an unbounded graph walk; choose bounded keyset reads or a size-bounded immutable result cache after measuring the query path.

Seed changed executable test symbols as hop zero before traversal. Preserve tests marked container/lifecycle separately from runnable cases. Display exact and heuristic reachability using N2's path-wide evidence. Compact prints the predecessor's readable name when useful, no repeated seed hash. JSON retains evidence IDs. Consume the [CT plan Task 6](2026-09-07-ct-agent-usefulness.md)'s provider selector for project-specific runner arguments and attested Scale status; unsupported selection says why and never guesses a command.

- [ ] A changed test body appears with `hop=0` even with no inbound callers.
- [ ] More than 40 impacted symbols cannot remove all likely tests from compact output.
- [ ] Every returned JSON page parses independently, fits its byte budget and contains complete rows; Unicode and long signatures do not split.
- [ ] Paging covers each row once; revision/query changes reject old tokens; tests-only mode performs no symbol rendering.
- [ ] Separate budgets and omitted counts remain truthful when traversal itself is truncated.
- [ ] CT-off workspaces still receive supported runnable selectors; heuristic rows never look exact.

## N2. Preserve uncertainty and normalize reference sites

Modify `src/Miller.Indexing/ReferenceEvidenceReader.cs`, `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`, `src/Miller.Core/Graph/GraphTraversal.cs`, `src/Miller.Core/Graph/SymbolGraph.cs`, and the graph adapters `src/Miller.Indexing/SqliteSymbolGraphIndex.cs` / `src/Miller.Indexing/SymbolGraphReader.cs`. Verify adapter symbols with Miller before editing. Keep `QueryTimeResolver.Resolve` uniqueness policy; change it only if producer facts can uniquely select a target. Tests: `tests/Miller.Tests/Indexing/ReferenceEvidenceReaderTests.cs`, `tests/Miller.Tests/Graph/SymbolGraphTests.cs`, `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs`; create `tests/Miller.Tests/Indexing/OverloadReferenceEvidenceTests.cs`.

Proposed new evidence classification carries `exact|heuristic|ambiguous` for the complete path, plus candidate target IDs for unresolved overload families. Publish scoped ambiguous evidence through trace and inspect even when exact references are zero. Do not broadcast one call to every overload as exact. Reject a homonym candidate only when extracted receiver/declared-type/import facts prove another target; a receiver spelling unequal to a class name is not proof. Unknown receiver evidence stays labeled.

Normalize duplicate exact/fallback rows by file, target identity and matching token span, preserving all provenance in JSON. Spanless relationship evidence can be grouped under a proven span only when containing symbol and kind agree uniquely. Same-line distinct calls stay distinct. Define total sites separately from rendered rows. Carry the weakest edge classification through each hop; if an independent all-exact path exists, prefer it deterministically without hiding alternative evidence.

- [ ] Two overloads produce a visible candidate family and zero invented exact references.
- [ ] One qualified receiver with a uniquely identified method stays exact; aliases and inheritance do not lose legitimate references.
- [ ] TryWrite on a proven ChannelWriter receiver cannot become an exact ScanFailureJournal reference.
- [ ] Exact then heuristic and heuristic then exact paths both remain heuristic; independent exact paths are handled deterministically.
- [ ] Two calls on one line remain two sites; duplicate exact/fallback evidence for one byte span becomes one display row.
- [ ] Full and bounded CLI/resident adapters agree on evidence classification; CT conservative Unknown behavior stays intact.

## N3. Correct trace diagnostics and useful starting points

Modify `src/Miller.Server/Tools/TraceTool.cs` (`RunRefs`, `RunPath`, `RunBridge`, `ResolveBridgeStart`, `TryBuildRouteDiagnostic`). Tests: `tests/Miller.Tests/Tools/TraceToolTests.cs`, `tests/Miller.Tests/Graph/SymbolGraphShortestPathTests.cs`.

Consume N2's distinct site projection. Compact omits opaque site IDs and states shown versus total unique sites, exact versus ambiguous/fallback. JSON keeps provenance and candidate sets.

Check the requested route's own observations before asserting frontend or backend presence. Preserve file-shaped target diagnostics: `RunBridge:1350` currently treats any slash-containing failed file target as a URL, replacing useful file diagnostics. Distinguish observed facts from built links, so a component with hx facts but no matched backend says so.

For class/file starts, gather callable member IDs and run a bounded multi-source shortest-path query with the chosen member named in output. Keep call mode strict. If only a dependency path exists, offer it as dependency evidence. A stored delegate value is not an invocation; no synthetic call edge without producer-backed invocation/target evidence.

- [ ] `/zzz/nope` reports no matching route facts even when other routes exist.
- [ ] Frontend-only, backend-only, both-unlinked and neither cases are distinct; no path-shaped file is reported as an HTTP route.
- [ ] A class/file start finds the same shortest path as its member start, with bounded expansion and explicit truncation.
- [ ] A method-group reference stays a reference; a dependency-only path is never relabeled call.
- [ ] Compact references retain source locations and enclosing symbols after removing site hashes.

## N4. Add complete member navigation and cheaper inspect pages

Modify `src/Miller.Server/Tools/InspectTool.cs` (`Inspect`, `RenderSymbolCompact`, corresponding JSON render) and `src/Miller.Server/Tools/CandidateOutput.cs`. Tests: `tests/Miller.Tests/Server/InspectToolTests.cs`; create `tests/Miller.Tests/Server/InspectMembersTests.cs` and `tests/Miller.Tests/Server/CandidateOutputTests.cs` only if no existing class owns those cases.

Proposed new `view=members` on inspect returns direct children with name, kind, full bounded signature, visibility and line, no body/ref/doc expansion. Use 40-row default and 100-row maximum for file/member listings, still under the existing byte cap; expose effective limit and a row continuation. Keep summary/overview/full behavior compatible. Rank public/protected members before private fields in overview, stable by line within a tier, and provide an actual members continuation instead of repeating `depth=full`.

Body continuation emits symbol identity, freshness and the next byte-exact body slice only. Compact documentation strips common XML presentation markup while preserving prose, code text and cref labels; retain raw docs in JSON. Complete constants omit a misleading unavailable-body section. Candidates show parent and signature; suggest a resolvable Parent.Member or file-qualified target, retaining IDs for actual overload ambiguity.

- [ ] A 116-member class can be fully enumerated without bodies or repeated relation sections, with no missing/duplicate children.
- [ ] Large caller-requested limits report their effective cap; other tools' row budgets are unchanged.
- [ ] A second body page preserves byte-exact concatenation and does not reread/render relation lists.
- [ ] Same-file ambiguity includes every bounded candidate's parent and a valid retry; overload IDs remain available.
- [ ] Markup-free compact docs keep literal angle-bracket code text; JSON raw docs remain available.

## N5. Guard edit input shape and preserve signature whitespace

Modify `src/Miller.Core/Editing/EditPlanner.cs`, `src/Miller.Server/Tools/EditService.cs`, and the body/signature descriptions in `src/Miller.Server/Tools/EditTool.cs`. Tests: `tests/Miller.Tests/Editing/EditPlannerTests.cs`, `tests/Miller.Tests/Server/EditToolTests.cs`; discover the existing EditService test class before extending it.

For body replacement compare the supplied prefix against the actual indexed/disk-verified declaration prefix; reject the clear duplicate-signature case with a body-only example. Do not pretend to validate arbitrary syntax or reject legitimate body expressions merely because they share a name. If the declaration prefix cannot be verified, report that limitation rather than guess it. Signature replacement preserves the original trailing whitespace before body_start unless the caller explicitly supplies replacement separation. Support LF, CRLF, tabs and Unicode without normalizing body bytes.

For no-match text edits return up to three bounded nearby current-file lines from the match engine's already examined candidates, with match rung and location. If no credible candidate exists say so. Do not trigger a workspace-wide index search just to fill a hint.

- [ ] Duplicate declaration text refuses before any write; valid C#, Python, Rust, JS and other applicable body-span fixtures remain accepted.
- [ ] Allman, same-line, expression-bodied and CRLF signatures retain their intended separation.
- [ ] Existing InsertAfter newline-preservation test remains green; no unsolicited newline “fix”.
- [ ] No-match preview returns bounded useful evidence and never applies a fuzzy candidate automatically.

## N6. Remove avoidable full-read costs

Modify `src/Miller.Server/Workspaces/WorkspaceEditContextFactory.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `src/Miller.Server/Tools/EditTool.cs`, and, only with measured need, `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs`. Proposed new file `src/Miller.Server/Workspaces/WorkspaceReadProjectionCache.cs` is the single cache owner if profiling justifies it. Tests: `tests/Miller.Tests/Server/EditToolTests.cs`, `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`; create `tests/Miller.Tests/Server/WorkspaceReadProjectionCacheTests.cs`.

Parse operation and validate required fields before resolving a complete symbol read. Split workspace/root validation and write dependencies from optional symbol projection. A disk-only replace must still prove workspace containment, current bytes, its allowed stale behavior and apply freshness; it need not hydrate every symbol. Indexed query/symbol selectors request the necessary projection lazily.

Instrument loads, misses, retained bytes and query phases. Compare warm and cold valid/invalid/text/symbol requests on one pinned artifact. If caching is warranted, cache immutable symbol/bridge projections by workspace, store generation/artifact identity, view and revision. Bound entry count/bytes, coalesce concurrent builds, release superseded entries and never cache a closure over a request session. A pending failed load is not a successful cached value. Retrieval context reuses this owner; it does not add another provider cache. Bound inspect suggestion candidate work after profiling; three displayed suggestions already exist and there is no semantic miss ladder to remove.

- [ ] Invalid operation and disk-only text preview produce zero complete-symbol load calls; apply safety tests still pass.
- [ ] Registered non-primary roots and never-bound primary processes work.
- [ ] Same revision concurrent readers share one immutable construction; generation or revision change rebuilds; removed workspace evicts.
- [ ] Cache entries do not retain SQLite transactions/read sessions or dead worktrees indefinitely.
- [ ] Bridge repeated calls avoid reconstructing an unchanged provider graph when the cache is enabled; measurements separate projection and graph costs.

## N7. Make rename coverage actionable without weakening proof

Modify `src/Miller.Server/Tools/EditService.cs` (`ExecuteRename`, evidence rendering and span recovery), `src/Miller.Server/Tools/EditRequest.cs`, `src/Miller.Server/Tools/EditTool.cs`. Keep `src/Miller.Core/Editing/RenamePlanner.cs` exact-span-only. Tests: `tests/Miller.Tests/Server/EditToolTests.cs`, `tests/Miller.Tests/Editing/RenamePlannerTests.cs`; create `tests/Miller.Tests/Server/RenameCoverageTests.cs`.

Use N2 classifications to separate exact selected sites, proven other-target sites, ambiguous sites and unsupported mentions. Exclude proven other-target sites before determining exact coverage; do not let unresolved unrelated-looking names disappear. Proposed new `exclude_sites` accepts reviewed source span identities bound to current file hash. Exclusions appear as explicit omitted coverage and cannot be used to claim a complete exact rename. Reject unknown/stale exclusions. Retain include_fallback as an explicitly risky mode; do not silently select it.

For spanless exact relationships, search only the fresh containing span and recorded line for a unique identifier token with the correct name/kind. Repeated tokens or missing containing boundaries refuse with a precise reason; no parser-like regex guesses in Miller. Prefer producer byte spans where recovery needs syntax.

Add bounded “not renamed” coverage for XML cref, comments, strings, Markdown and old-name-prefixed test names, using existing source regions/content inventory and symbol names. Classify plain text as mentions, never resolved references; include total/truncated/not-scanned status. Do not automatically modify prose/string literals. XML cref exact rewriting waits for producer-bound reference facts.

- [ ] Exact-resolved homonyms stay excluded; an unresolved ChannelWriter example remains a visible refusal/candidate, never an unsafe implicit rename.
- [ ] Spanless unique token can be proven; two same-name tokens cannot be guessed.
- [ ] Coverage reports include unsupported regions and missing coverage even when rename cannot proceed.
- [ ] Preview and apply use the same selection, revalidate exclusions/hash, and preserve all rollback guarantees.

## N8. Add transactional multi-hunk edits

Modify `src/Miller.Server/Tools/EditRequest.cs`, `src/Miller.Server/Tools/EditTool.cs`, `src/Miller.Server/Tools/EditService.cs`, and `src/Miller.Server/Hosting/EditApplier.cs` if its existing multi-file apply seam needs extension. Proposed new file `src/Miller.Server/Tools/EditBatchPlanner.cs`; tests in proposed `tests/Miller.Tests/Server/EditBatchTests.cs` and existing `tests/Miller.Tests/Server/EditApplierTests.cs`.

Proposed NEW `operation=batch` with an `edits` array of existing operation arguments on the existing edit tool. One workspace, one preview/apply decision, maximum 100 operations, and the existing response byte budget. Disallow nested batches. Evaluate all operations against the same original fresh snapshots; reject overlapping spans and order-dependent requests rather than silently interpreting later edits against rewritten text. Disjoint changes to one file combine into one planned write; duplicates either coalesce identically or refuse conflicting replacements. If rename participates, its expanded spans join the same conflict checks. Return per-operation proof and one combined diff; output truncation must not imply incomplete validation.

- [ ] Thirteen distinct parameter-addition sites produce one preview with 13 proofs, zero writes.
- [ ] A failing member, overlapping edits, stale file, path escape or changed disk hash prevents the batch write.
- [ ] A multi-file failure rolls back as today and accurately reports partial rollback failure.
- [ ] Cross-workspace targets and nested batches refuse; non-ASCII byte spans and repeated-text selectors retain current proof rules.

## N9. Repair producer facts and adopt them with language parity

Producer paths relative to `/home/murphy/source/julie-extractors`: modify `crates/julie-extractors/src/csharp/identifiers.rs` (`is_csharp_type_usage_identifier`) and `crates/julie-extractors/src/tests/csharp/identifier_extraction.rs`; check the sibling `crates/julie-extractors/src/razor/identifiers.rs` predicate as part of the same parity work. For structured interpolation metadata, own `crates/julie-extractors/src/base/framework_structural_facts/markup.rs` and tests `crates/julie-extractors/src/tests/structural_facts.rs`, serialized with the retrieval plan's MapMethods/dictionary task.

Add type_usage for bare return identifiers through the grammar's `returns` field, alongside existing nullable/generic/qualified/array forms. Build a producer language matrix from its supported-language inventory and establish return-type positions for every applicable parser. Languages without explicit return types record non-applicability, not fake rows.

The existing raw htmx `attribute_value` and `target_path` must remain. Proposed NEW metadata for parser-proven templated paths names literal segments and dynamic segments plus a normalized route template and uncertainty. Use parsed Razor expression boundaries; do not truncate at the first `@` and discard `/tests/start`, or treat two different suffixes as one route. Apply the same contract across applicable supported markup/component languages. Expressions that can add slashes or arbitrary paths remain partial/unknown instead of exact parameter matches.

Consumer changes after producer verification/pin: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`, `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs`, `src/Miller.Core/Resolver/RouteNormalizer.cs` only for generic fact normalization, and `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`. Update `scripts/julie-pins.json` through the normal restored-pin process only once an approved producer artifact exists. Do not silently release the dependency as part of plan execution.

- [ ] The real `Result Bare()` probe gains its return type_usage without duplicating nullable, generic or constructor rows.
- [ ] All applicable supported languages have a real-extract fixture and exact span assertions; non-applicable cases are explicit.
- [ ] WorkspaceTestsPanel's three existing hx-post facts stay visible; enable/start/run preserve different suffixes and uncertainty.
- [ ] Missing MapMethods endpoints are fixed by the retrieval producer task, not hidden by a consumer wildcard.
- [ ] No syntax recognition moves into Miller; existing literal routes and confidence/verb-unknown semantics stay unchanged.
