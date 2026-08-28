# Continuous-Testing Language Gap Audit

This audit ranks language gaps that block better continuous testing in `/home/murphy/source/miller`.

It answers one question: what must change in Julie facts or in Miller mapping and providers before Miller can cover more languages, or cover the current languages more accurately.

It does not treat a missing focused runtime run as a language gap. Runtime proof is a later host record. It is not extractor debt.

## Snapshots

- Julie: `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan`, branch `worktree-ct-language-audit-plan`, commit `2ea9b0daa2e736f9248d8caf4c475e47dea0d522`.
- Miller: `/home/murphy/source/miller`, branch `main`, commit `53bba8c166b56d5c4c29721a4e6619760397aeed`.

`node scripts/language-data-quality-report.mjs --strict` reports 39 language rows, 0 `silent_cells`, 0 `quality_bar_debts`, and 45 `open_gaps`. Those 45 gaps are mostly framework-local or other quality domains. They are not a Miller language-count ranking.

## What Miller runs today

Miller registers five provider groups in `ContinuousTestProviderFactory.CreateDefault`:

| Ecosystem | Framework values | Julie source languages in that family |
|---|---|---|
| .NET | `xunit`, `nunit`, `mstest`, `dotnet` | C#, Visual Basic, Razor. F# projects are discovered. F# source is not extracted. |
| Rust | `cargo`, `rust` | Rust |
| Python | `pytest`, `python` | Python |
| JavaScript and TypeScript | `vitest`, `jest`, `node-test` | JavaScript, TypeScript, JSX, TSX. Vue is not mapped. |
| QML and Qt | `qt-quick-test` | QML, CMake/CTest only |

Miller documents this set in `docs/continuous-testing.md`. Go, Ruby, Java, PHP, and every other toolchain have no provider.

## What Miller consumes from Julie

Julie publishes names, parent links, spans, deterministic symbol IDs, `metadata.test_role`, and grouped flags `is_test`, `test_container`, and `test_lifecycle`.

Miller's CT adapter keeps less:

- `IndexedSymbol` still carries test-role evidence.
- `CtFactAdapter.ToSymbolFact` copies only `IsTest` into `CtSymbolFact`.
- `LanguagesAreCompatible` accepts an exact language match, or C# with Razor. It does not treat `jsx` as JavaScript, `tsx` as TypeScript, or `vbnet` as C#.
- `LanguageFromPath` maps `.jsx` to `javascript` and `.tsx` to `typescript`. It omits `.vb`, `.mjs`, `.cjs`, `.mts`, and `.cts`.
- `ProviderTestCase` has runner identity (`Id`, `Selector`, `FullyQualifiedName`). It has no Julie `symbol_id` field.

Julie `is_test` is enough for Miller to know a symbol is a test. Exact five-role goldens improve later selection quality. They are not the first blocker for adding a language to Miller CT.

## Recommended order

1. Fix Miller language-family mapping on the providers that already exist. This is the cheapest language-count gain.
2. Keep Julie `IsTest`, `symbol_id`, parent, and `test_role` through `CtSymbolFact` and provider cases. This improves every current language.
3. Add a Go provider after the mapping and identity work. Julie already emits useful Go test facts. Miller already maps `.go`.
4. Add other providers only after the shared mapping and identity bugs are fixed. Otherwise each new language repeats the same selection failures.
5. Close Julie framework-local gaps only when they affect the primary runner Miller will actually invoke.

Do not start a broad Julie test-detector rewrite for this ranking.

## Ranked gaps

### 1. Mapping bugs on existing providers

These languages already have a Miller provider family. Selection can still drop them.

| Language | Miller today | Julie test facts | Blocking gap | Owner |
|---|---|---|---|---|
| Visual Basic (`vbnet`) | `.vbproj` is discovered with the .NET group | Grouped `test_case`, `test_container`, `test_lifecycle` goldens exist | `LanguageFromPath` ignores `.vb`. `LanguagesAreCompatible` then returns false. | Miller |
| JSX | JavaScript provider group exists | Grouped goldens exist. No direct `test.each` golden. | Julie language is `jsx`. Path mapping emits `javascript`. Those strings do not match. | Miller |
| TSX | JavaScript/TypeScript provider group exists | Grouped goldens exist. No direct `test.each` golden. | Same family mismatch: `tsx` vs `typescript`. | Miller |
| JavaScript modules | JavaScript provider group exists | Grouped goldens exist | `.mjs` and `.cjs` are omitted from `LanguageFromPath`. | Miller |
| TypeScript modules | JavaScript/TypeScript provider group exists | Grouped goldens exist | `.mts` and `.cts` are omitted from `LanguageFromPath`. | Miller |
| Vue | No `.vue` mapping and no Vue-specific provider proof | Grouped goldens exist | Miller has no Vue project path. Do not treat this as a Julie parser gap. | Miller |
| Razor | .NET group exists; C# and Razor are compatible | Grouped goldens exist | Shared identity and role drop, not a missing provider. | Miller |
| F# | `.fsproj` is discovered | No F# source extractor and no capability row | Parser work is a separate Julie plan. A `.fsproj` file does not create an F# source row. | Julie, later |

Primary evidence:

- `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs:1090-1102,1250-1269`
- `crates/julie-extractors/src/language_spec/specs.rs:3-319`
- Visual Basic capability row in `fixtures/extraction/capabilities.json`

### 2. Shared Miller fact loss

These defects hit every current language. They are not per-language extractor debt.

| Defect | Effect | Evidence |
|---|---|---|
| `CtSymbolFact` drops detailed roles | Selection sees `IsTest` only. Exact `test_role`, container, and lifecycle flags never reach the selector. | `ICtFactSource.cs:40-51`, `CtFactAdapter.cs:245-257` |
| Provider cases lack Julie identity | A discovered runner case cannot join to the Julie symbol that named it. | `ProviderContracts.cs:381-416` |
| False `KnownEmpty` from language mismatch | A real test file can look like "no tests apply" when Julie language and path language disagree. | `ContinuousTestImpactSelector.cs:200,1090-1097,1250-1269` |
| Generic `.NET` routing | Detection can emit `dotnet` while the generic route accepts only MSTest and NUnit. | `ContinuousTestProjectInventory.cs:1155-1233`, `DotnetTestProvider.cs:131-161,2096-2099` |
| 64 KiB substring project detection | Large or unusual project files can be misclassified. | `ContinuousTestProjectInventory.cs:1436-1453` |

### 3. Existing providers with bounded discovery

These are quality limits, not missing languages.

- Jest and Vitest use `.test` and `.spec` stems.
- Python creates one provider case per file.
- QML uses only CMake/CTest. qmake is unsupported.
- xUnit v2 is incompatible with the current execution path. That is Miller or runtime evidence, not C# extractor debt.

Sources: provider implementations and Miller `docs/continuous-testing.md`.

### 4. First new provider: Go

| Check | Status |
|---|---|
| Miller provider | Missing |
| Path mapping | `.go` is already in `LanguageFromPath` |
| Julie roles, imports, identifiers, and calls | Useful |
| Exact literal `t.Run` child names | Missing. Current Go call extraction routes to Ginkgo. `GoExtractor` handles `call_expression` only through `extract_ginkgo_call`. |
| Runtime slash selectors | Out of scope for Julie |

Go is the best first new Miller ecosystem after the mapping and identity work. The Julie gap that matters for focused selection is literal `t.Run` child identity. It does not block a first whole-file or whole-function Go provider.

Evidence: `crates/julie-extractors/src/go/mod.rs:293`, `src/tests/go/build_tags.rs:48-50`, [Go `testing.T.Run`](https://pkg.go.dev/testing@go1.27.0#T.Run).

### 5. Source languages with Julie facts and no Miller provider

Julie already claims grouped test-role support for these languages. Miller has no provider. The blocker is Miller, except where a named Julie gap is called out.

| Language | Julie grouped test facts | Named Julie gap that stays local | Miller |
|---|---|---|---|
| C | Yes | None for a first C unit path | Missing provider |
| C++ | Yes | None for a first GoogleTest/Catch path | Missing provider |
| Java | Yes | Cucumber and suite gaps stay Java-local | Missing provider |
| Kotlin | Yes | Kotest table gap stays Kotlin-local | Missing provider |
| Scala | Yes | Nested FunSpec is proven. Parameterized and teardown goldens are incomplete. | Missing provider |
| PHP | Yes | Codeception and PHPSpec stay PHP-local | Missing provider |
| Ruby | Yes | Shared-example and parameterized proof stay Ruby-local | Missing provider |
| Swift | Yes | Swift Testing traits stay Swift-local | Missing provider |
| Dart | Yes | None named for a first Dart test path | Missing provider |
| Elixir | Yes | None named for a first ExUnit path | Missing provider |
| Erlang | Yes | None named for a first EUnit path | Missing provider |
| Lua | Yes | None named for a first Busted-style path | Missing provider |
| Bash | Yes | None named for a first shell-test path | Missing provider |
| PowerShell | Yes | None named for a first Pester path | Missing provider |
| GDScript | Yes | None named for a first GUT-style path | Missing provider |
| Zig | `test_case` only | Container and lifecycle are `not_applicable` | Missing provider |
| R | Cases and container exist | Lifecycle is marked `not_applicable` without a primary testthat source. Official testthat still documents `setup()` and `teardown()`. | Missing provider |

Scala and R are the only rows in this group with a Julie honesty problem that should be fixed before Miller treats their lifecycle or parameterized facts as complete. They still do not outrank Go as the first new provider.

### 6. Not executable test sources in this product

These 10 `LANGUAGE_SPECS` rows are not Miller CT source candidates:

QMLDIR, CSS, HTML, JSON, Markdown, Regex, SQL, TOML, XML, YAML.

XML can hold `.fsproj`. That does not make XML a test language and does not create an F# extractor.

## Julie capability bar vs Miller need

The capability gate proves three grouped units: `test_case`, `test_container`, and `test_lifecycle`. It does not separately prove `parameterized_test`, `fixture_setup`, and `fixture_teardown`.

Miller CT selection currently uses `IsTest`. Raising Julie to five exact roles is useful later. It is not what blocks Miller from adding Visual Basic, JSX, TSX, or Go.

A follow-on Julie packet for JSX, TSX, Go `t.Run`, Scala parameterized/teardown, and R lifecycle lives in `docs/plans/2026-08-27-continuous-testing-extractor-evidence.md`. That packet must not modify Miller.

## Runtime

This audit recorded no current OS, executable, version, package, discovery, and focused-run proof for any candidate. That keeps runtime status unknown. It does not change the language ranking above. A binary on `PATH` is not enough.

## Primary sources

- `crates/julie-extractors/src/base/kinds.rs:6-28`
- `crates/julie-extractors/src/test_detection.rs:44-61`
- `crates/julie-extractors/src/tests/capability_matrix.rs:1227-1229,1885-1907`
- `crates/julie-extractors/src/language_spec/specs.rs:3-319`
- `crates/julie-extractors/src/go/mod.rs:293`
- `docs/architecture/continuous-testing-evidence-boundary.md`
- `/home/murphy/source/miller/docs/continuous-testing.md:17-58`
- `/home/murphy/source/miller/src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs:44-101`
- `/home/murphy/source/miller/src/Miller.Indexing/Testing/ICtFactSource.cs:40-51`
- `/home/murphy/source/miller/src/Miller.Indexing/Testing/CtFactAdapter.cs:245-257`
- `/home/murphy/source/miller/src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs:1090-1102,1250-1269`
- `/home/murphy/source/miller/src/Miller.Testing/Contracts/ProviderContracts.cs:381-416`
- [Go `testing.T.Run`](https://pkg.go.dev/testing@go1.27.0#T.Run)
- [testthat setup and teardown](https://testthat.r-lib.org/reference/teardown.html)
