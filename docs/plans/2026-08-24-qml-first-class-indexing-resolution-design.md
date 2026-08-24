# QML First-Class Indexing and Resolution Design

## Outcome

Miller will ingest Julie’s QML-family artifact without special-case loss, expose QML evidence through its existing tools, and resolve QML component uses according to directory and module visibility instead of global name uniqueness.

## Product Boundary

- Julie owns parsing and extraction; Miller consumes the pinned artifact contract.
- Miller computes workspace-visible QML component candidates at query time.
- No raw-source QML parser is added to Miller.
- Search, inspect, trace, patterns, and edit continue to use their existing public interfaces.
- Semantic-card eligibility remains kind-driven and language-neutral in this project.

## Architecture Quality

**Affected modules:** extractor/schema pins, revision fact loading, resolution facts, query-time resolution, pattern facts, tool integration fixtures, and release compatibility tests.

**Caller-facing interface:** existing Miller search/inspect/context/trace/impact/patterns/edit surfaces. Internally, `IResolutionFacts` gains a QML visibility query returning typed candidates rather than raw structural-fact rows.

**Depth/locality check:** artifact decoding remains in `Miller.Indexing`; resolution policy remains in `Miller.Core`. QML module rules are isolated behind a typed catalog and do not leak SQLite, JSON, or structural-fact schemas into the resolver.

**Test surface:** store-backed and artifact-backed revision caches, pure resolver tests, and end-to-end tool tests over the same multi-file QML fixture.

**Seams/adapters:** `RevisionFactCacheLoader` translates import-symbol metadata plus `qmldir`/`.qmltypes` artifact facts into `QmlVisibleType` records. `IResolutionFacts.QmlTypesVisibleTo(versionId)` is the sole resolver-facing QML module seam.

**Rejected shortcuts:** global unique-name matching, translating `qml.import_statement.v1` into generic `ImportBinding`, teaching the core resolver structural-fact JSON, resolving from directory names without manifest evidence, store-only tests, and language-specific semantic-card eligibility.

**Architecture risk:** high. Resolution correctness crosses the extraction contract, two cache load paths, import precedence, and generic query-time resolution.

## Artifact Consumption

- Upgrade Miller’s extractor version, schema pin, contract pin, fixture artifacts, and compatibility assertions together after the Julie release artifact is available.
- Generic `ImportBinding` rows continue to come only from import-symbol metadata.
- QML manifest and typeinfo structural facts are decoded by indexing adapters into typed QML visibility facts.
- Unknown future QML fact versions fail or degrade according to the existing schema/contract policy; they are never silently guessed.
- Both SQLite store reads and immutable artifact reads generate byte-for-byte equivalent logical visibility candidates.

## QML Visibility Model

`QmlVisibleType` carries the minimum resolver input:

- consumer version id;
- target version id and symbol key;
- exported QML type name;
- module URI or directory scope;
- source component path;
- optional version range/revision;
- singleton/internal status;
- import alias, if the candidate is visible only through an alias;
- evidence provenance and source span.

Visibility rules:

1. Same-file declarations remain the strongest local evidence.
2. Same-directory QML components are visible under Qt directory-import rules.
3. Explicit directory imports add components from the imported directory.
4. URI imports bind only types exported by matching `qmldir`/`.qmltypes` module evidence, with alias and version constraints applied.
5. `internal` types do not escape their module/directory scope.
6. Alias-qualified uses resolve only through the matching alias.
7. Multiple equally valid candidates remain unresolved/ambiguous with preserved evidence; Miller does not pick by global uniqueness.
8. Legacy global matching is not used for QML component instantiation.

The resolver consumes extracted pending `instantiates` relationships and QML visibility candidates. It preserves existing confidence/provenance semantics and never emits both a QML module result and a generic fallback result for the same use.

## Tool Evidence

- Search returns QML components, functions, properties, signals, and relevant identifiers lexically through existing indexes.
- Inspect renders extracted QML symbol/type/relationship details without a QML-only output schema.
- Trace follows resolved and unresolved instantiation/import evidence with existing confidence labels.
- Patterns exposes registered `qml.*` and `qmldir.*` facts through `PatternFactsReader`.
- Edit proves `.qml` readiness and span-safe replacements through existing language/edit policy.
- Semantic-card generation remains unchanged. Expanding property/event eligibility is a separate corpus-versioned decision, not hidden inside QML support.

## Testing

- Consume the Julie multi-file QML module golden as the authoritative integration fixture.
- Add paired store/artifact cache tests with identical expected `QmlVisibleType` records.
- Add pure resolution matrices for aliases, versions, internal types, directory imports, same-directory visibility, ambiguity, missing manifests, and duplicate names across modules.
- Add end-to-end search, inspect, trace, patterns, and edit tests over QML files.
- Run the fast suite at task boundaries and the full Scale suite once because extraction/index/resolution paths change.
- Run Windows verification on the NTFS guest from a clean commit because artifact paths and file spans are cross-platform contracts.

## Acceptance Criteria

- [ ] Miller accepts the released Julie QML/qmldir/.qmltypes artifact contract and rejects incompatible pins honestly.
- [ ] Store and artifact fact loaders produce identical typed QML visibility evidence.
- [ ] QML component resolution follows module/directory/alias/version/internal rules without global unique-name fallback.
- [ ] Ambiguity and unresolved cases remain explicit and evidence-backed.
- [ ] Search, inspect, trace, patterns, and edit have QML integration coverage.
- [ ] Semantic corpus generation and eligibility remain unchanged.
- [ ] Fast, full Scale, compatibility, and Windows gates pass at the same final commit.
