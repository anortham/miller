# M1 reader-retention integration qualification

Status updated after the user's release/pin approval: implementation is on `feature/reader-retention-integration`; published julie-extract 2.40.0 is adopted in `75e6d9b4`. The original source-only qualification below remains historical evidence. Installed-binary qualification is recorded in the following section; source injection is no longer required. Native adverse-process-identity limits remain explicit.

## Published producer adoption

The user approved release, pin adoption and subsequent local M1 merge. [julie-extract v2.40.0](https://github.com/anortham/julie-extractors/releases/tag/v2.40.0) published at 2026-09-05 19:58:05 UTC from `adfb72a5e57a0b34629943865a2ac2d9ee218901`. [Source CI 33987008736](https://github.com/anortham/julie-extractors/actions/runs/33987008736) and [release run 33987969809](https://github.com/anortham/julie-extractors/actions/runs/33987969809) passed all jobs. The four archives were downloaded, their hashes matched the live asset digests, and each embedded binary matched its packaged checksum.

`scripts/julie-pins.json`, `MillerExtractContract.PinnedJulieExtractVersion`, three existing version assertions, README and the third-party notice now agree on 2.40.0. Normal restore scripts fetched the Linux and Windows published URLs and verified the archive pins. No source override or missing-tool escape hatch was used. Semantic pins and runtime are unchanged.

Published Linux binary SHA-256: `c92aec10146e7178aa5e2762c095fbb65557af2687efacb462cfa474cf0b0310`.
Published Windows binary SHA-256: `e2dd8a1edce6597e09942d911d72c1ed4d651c696b8b0be47b7317a91e57268e`.
Archive digests are committed in the pin file and Julie's `docs/release-evidence/2026-09-05-v2-40-0-release.md`.

Installed-pin verification at `75e6d9b45f4cea0d8d8c16df8048d31273dbbf0f`:

- Three explicit version checks failed with expected 2.40.0 / actual 2.39.0 before the constant update; the focused pin/schema/layout/CLI scope then passed 41 tests in 150 ms.
- Full Linux Release fast suite passed 9,859 tests, nine skips, zero failures in 38 seconds.
- Full Linux installed Scale passed 216 tests, 24 skips, zero failures in 85 seconds. Command: `scripts/test.sh scale --no-build --no-restore --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=m1-installed-2.40-scale.trx' --results-directory <gate-directory>`, with no source-binary override. The TRX is retained beside the earlier source gate in the scratch gate directory below.
- Final `dotnet build Miller.slnx -c Release --no-restore` passed with zero warnings/errors in 19.92 seconds.
- Windows guest NTFS was synced to the exact full commit above, then `powershell -File scripts/restore-julie-extract.ps1` restored the published asset. `dotnet test -c Release --filter ...` passed 158 tests, three skips, zero failures in 117 seconds. The filter joined `RealProducerReaderRetentionScaleTests`, `ReaderRetentionLanguageScaleTests`, `ScaleTestSupportTests`, `ScaleTraitConventionTests`, `StoreWorkspaceIndexProviderScaleTests`, `VersionAwareLeadershipScaleTests`, `LiveTestRoleEvidenceScaleTests`, `LiveReferenceResolutionScaleTests`, `FamilyStoreReadSessionTests`, `MillerExtractContractTests`, `JulieSchemaGateTests`, `SemanticSidecarLayoutTests` and `Capabilities_Json_ReportsErosContractSurface` with `FullyQualifiedName~` predicates. Skips were two Unix-only restore checks and the retired `store resolve` command. No source override was used. This is an affected-scope Windows run, not a new whole-fast-suite claim.

These installed-binary gates close the incompatible-pin blocker and permit the user-approved local main integration. Later changes to this finding, the plan status, documentation map and checkpoints are documentation-only. The already-green executable/test tree is not rerun for those edits. Main's running MCP process is not replaced by a source merge; the user still needs to rebuild and restart.

The following sections describe the earlier implementation/source qualification. References to a 2.39 pin or publication approval being absent describe that earlier stage, not the adopted pin above.

## What changed

Family-store reads now acquire producer-owned retention before opening generation SQLite connections. Each session serves the exact admitted family, view, generation, manifest and log interval. A later publication cannot retarget that session. Metadata discovery is the narrow exception described in [the discovery finding](2026-09-05-m1-discovery-boundary.md).

The shared registration registry renews leases and retries owed cleanup. SQLite connections close before release. If closure cannot be established, protection remains and additional connection creation is refused until cleanup succeeds. A cleanup failure does not replace the original query/open failure. Release uncertainty remains scoped to the original registration. Legacy reads perform no registration, and incompatible enabled store reads fail closed without stale legacy fallback.

The selected producer flows through factory, bootstrap, refresh, CLI, dashboard and CT reads. CT's private executable image includes the selected extractor and hashes its content. Reader registration does not activate semantic retrieval or CT. No MCP tool or wire field was added.

Implementation commits, in order:

- `a70c305c`: typed reader runner and lifecycle.
- `967763d3`, `29990f12`: approved discovery boundary and single-snapshot metadata discovery.
- `1b2b0f3c`: concrete session admission and connection ownership.
- `f21c0eae`, `2a289e0a`: selected-producer routing through workflows and consumers.
- `77383be8`: explicit tests-only source binary selection.
- `4233d014`: bounded failed cleanup, primary-error preservation and narrow metadata.
- `17ce7c6f`: native producer retention tests and internal bootstrap tool-selection seam.
- `b2cc0870`: real admission with all 40 supported languages.
- `f8f4ef7b`: serialize the existing process-global bounded-facts flag test.
- `e24fc4a1`: assert release-owed state while an actual maintenance fence remains.
- `f006918a`: qualify existing fixtures with the selected source producer, preserving default pin assertions.

The plan and its checked task-level acceptance are in [M1](../plans/2026-09-04-reader-retention-integration.md). Source-gate fixture corrections following these commits preserve default pinned-version assertions and consistently select the injected producer for all reads, including rollback and leadership.

## Producer provenance and package boundary

The Linux source binary reports `julie-extract 2.40.0`. It was built from Julie main `3b3e5b6f03b724448df9012bb75224e99ca68f5d` with:

```sh
CARGO_TARGET_DIR=/home/murphy/source/julie-extractors/.worktrees/reader-retention-contract/target cargo build --release --locked -j4 -p julie-extract-cli
```

Its path is `/home/murphy/source/julie-extractors/.worktrees/reader-retention-contract/target/release/julie-extract`; SHA-256 is `8edb83508478bb8967675fd19590da830536b00ba9f10a1e6f2d3d0c8cb55b16`. The containing worktree's HEAD is `ecd021c05d774068423911877ebf254eac6ec0cf`, not the binary's build source commit.

Windows used the same source commit, built natively at `C:\work\julie-extractors\target\release\julie-extract.exe`, version 2.40.0, SHA-256 `4cf8225043d15b491d1f5328aadd7538bc971020650a1392415b9f82a7c6f3da`.

Tests select it through `MILLER_TEST_JULIE_EXTRACT`. That variable is tests-only. Missing or relative explicit paths refuse rather than falling back. Without an override, executable-version assertions still require the pinned version. With an override, artifact metadata must match the actual selected executable's reported version.

Miller's committed pin and both checkout `.tools` directories remain on 2.39.0. That producer lacks this reader contract. The running MCP server also remains the previously built main binary, not this feature branch. Source-injected success does not qualify installed packages. GitHub's latest-five release listing on 2026-09-05 still named v2.39.0 as Latest; a separately approved release/pin task must verify the actual published artifacts and checksums before adoption.

## Verification ledger

These are distinct scopes, not numbers to sum into a fictitious single run. Later commits after `4233d014` change tests or internal test selection, not the production retention design.

| Scope | Revision | Result |
| --- | --- | --- |
| Task 4 consumers | `2a289e0a` | 713 passed, 1 OS skip; final DI/provider scope 141 passed; dashboard 116 passed; CT shadow copy 27 passed |
| Task 5 | `4233d014` | 206 distinct focused passes; 26 added cases; five real SQLite regressions demonstrated failing before fixes |
| Linux Release solution build | `b2cc0870` | 0 warnings, 0 errors, 18.74 seconds |
| Final Linux Release solution build | `f006918a` | 0 warnings, 0 errors, 19.01 seconds |
| Linux Release fast suite | `e24fc4a1` | 9,852 passed, 9 skipped, 41 seconds |
| Windows fast suite | `17ce7c6f` | 9,826 passed, 35 skipped, 11 minutes 10 seconds |
| Windows source retention, language matrix and missing-view bootstrap | `b2cc0870` | 13 passed, 0 skipped, 47 seconds |
| Linux native retention after maintenance-fence correction | `e24fc4a1` | 11 passed, 3.7106 seconds |
| Source-version fixture corrections | after `e24fc4a1` | 37 distinct focused passes, 1 pre-existing retired-command skip; all nine original failed methods passed |
| Linux full source Release Scale | `f006918a` code | 216 passed, 24 skipped, 0 failed, 74 seconds |
| Final Windows affected source scope | `f006918a` | 118 passed, 1 pre-existing retired-command skip, 0 failed, 3 minutes 8 seconds |

The Linux build command was `dotnet build Miller.slnx -c Release --no-restore`. The final recorded fast command was `dotnet test -c Release --no-build --no-restore --logger 'console;verbosity=minimal'`. Windows runs used `win-test sync miller <full-sha>` followed by `win-test run miller -- powershell -Command ...`, on guest NTFS, never a shared filesystem. The full Windows fast scope used `dotnet test --filter 'Category!=Scale'`.

Broad source Scale initially exposed nine existing tests mixing source 2.40 scanners with pinned 2.39 readers/version assertions. Those fixtures now propagate the selected binary through every relevant bootstrap, context and read. No skip or compatibility assertion was removed. A separate full-fast failure was an unisolated test changing `MILLER_BOUNDED_FACTS`; its class now uses the existing nonparallel environment collection. The production cleanup logic did not need changing for that race.

The full source Scale command was `MILLER_TEST_JULIE_EXTRACT=<source-binary> scripts/test.sh scale --no-build --no-restore --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=m1-source-scale-final.trx' --results-directory <gate-directory>`. The local gate directory is `.razorback/sdd/2026-09-04-reader-retention-integration-dca83d240e78/gates`. Skips are recorded rather than counted as passes, including absent optional provider/toolchain fixtures, OS-specific tests and the retired `store resolve` command.

The final Windows command selected the source binary with `Set-Item Env:MILLER_TEST_JULIE_EXTRACT 'C:\work\julie-extractors\target\release\julie-extract.exe'` and ran `dotnet test --filter` over `RealProducerReaderRetentionScaleTests`, `ScaleTestSupportTests`, `ScaleTraitConventionTests`, `StoreWorkspaceIndexProviderScaleTests`, `VersionAwareLeadershipScaleTests`, `LiveTestRoleEvidenceScaleTests`, `LiveReferenceResolutionScaleTests` and `FamilyStoreReadSessionTests`, joined by `FullyQualifiedName~...|FullyQualifiedName~...`. This Debug run covers the final fixture and maintenance-fence corrections. The unchanged language matrix retains its earlier Windows result. The unchanged Linux fast scope retains its `e24fc4a1` result, with the seven added helper cases covered by the later focused run. Neither earlier full fast result is represented as a fresh whole-suite run at `f006918a`.

## Native retention evidence

`RealProducerReaderRetentionScaleTests` uses the real producer transport and SQLite registration/root facts, not fake admission fields. It observes the unopened SQLite connection's state transition, proving acquisition precedes its first open. It checks exact manifest, entries, referenced versions, physical generation and retained log interval.

The ordinary session performs one acquire, five symbol queries and one release. Query calls do not spawn producer processes. A lost committed reply retries the same nonce and produces one registration row, not duplicate ownership. Promotion between acquire and first open still serves the admitted snapshot. Publishing a newer view does not retarget an existing reader. GC and retirement respect its roots; retirement succeeds after release. Real concurrent acquire/GC, promotion and retirement subprocess tests accept only contract-valid committed or refused outcomes. Fixtures with manually inserted fence rows are explicitly separate from true concurrent subprocess tests.

A preliminary isolated fixture at `/tmp/miller-m1-producer-fixture.6Df1J6` used family `767b6bf2-5213-41f4-9d3d-de62ee14f3ca`, view `m1-fixture`, generation `gen-001`, manifest 1. Its manifest hash was `1db485d362e8999d08330a0eae113c8da4f20f68acc7b442497336047e32293a`; log bounds were 1 through 9, admitted floor 3 and served revision 9. The public factory probe had PID 2688241, one registration while open and zero after disposal, with eight symbols. Open measured 54 ms and total probe 64 ms. These isolated measurements are not a claim about general MCP latency.

## Bounded maintenance cleanup finding

One actual concurrent promotion returned `StalePlan` while its maintenance intent remained live. The subsequent authenticated reader release returned `Busy`. Miller had zero open SQLite connections and retained one `ReleaseOwed` handle with the correct roots. Immediate zero registrations would have been an incorrect test requirement.

The producer's maintenance lease is 60 seconds. Its foreign-intent check honors the persisted expiry even after the owner exits. Best-effort destructor cleanup restores source floor/mirrors before clearing ownership; a restoration error can skip that clear. The unchanged binary does not report the hidden destructor error, so the exact underlying cleanup error is unproven.

Two diagnostic reproductions recovered through authenticated release after the actual persisted deadline. The writer floor stayed 2.40.0 before, during and after the fence; no live writer leases remained afterward. An expired intent row could remain, but reader registrations and Miller handles reached zero. The durable test checks the actual Busy result, closed connections, exact retained roots, bounded persisted deadline and unchanged writer floor before waiting for that deadline and ticking the existing registry. It does not delete the fence or add a production retry timer. This Scale case can naturally take about a minute.

Producer cleanup-error observability remains follow-up work. These results do not prove every writable-import recovery path, every race interleaving, forced owner death, PID reuse, or unknown kernel process identity. Normal native ownership and birth identity were observed; simulated renewal/identity tests establish Miller's fail-closed behavior but are not native OS identity proofs. Actual Miller interrupted rollback/export and leadership handoff are covered by the corrected existing Scale fixtures; absence of a producer `maintain rollback` command is not a reason to omit that coverage.

## Language parity

`ReaderRetentionLanguageScaleTests` imports representative upstream basic fixtures from all 40 catalog languages, then reads through the public admitted-session factory. The actual catalog, fixture language set and served symbol language set must agree. Coordinator counts are zero before, one for the exact PID/view/generation/manifest during, and zero after disposal.

The real query `SELECT language, kind, COUNT(*) FROM symbols GROUP BY 1,2 ORDER BY 1,2` returned 1,026 symbols in 237 language/kind groups. Coverage includes bash, c, cpp, csharp, css, dart, elixir, erlang, fsharp, gdscript, go, html, java, javascript, json, jsx, kotlin, lua, markdown, php, powershell, python, qml, qmldir, r, razor, regex, ruby, rust, scala, sql, swift, toml, tsx, typescript, vbnet, vue, xml, yaml and zig. This establishes language-neutral reader admission, not exhaustive parser or fact-kind coverage.

| Language | Kind=count |
| --- | --- |
| bash | constant=1, function=3, variable=12 |
| c | field=2, function=7, struct=7, variable=12 |
| cpp | class=2, constructor=1, field=1, function=5, method=4, struct=3, variable=17 |
| csharp | class=7, constant=2, constructor=2, field=6, interface=1, method=10, namespace=1, property=5, variable=13 |
| css | function=1, property=8, variable=7 |
| dart | class=5, constructor=3, field=1, function=6, method=4, variable=21 |
| elixir | function=11, import=3, module=1, type=1, variable=17 |
| erlang | constant=2, field=4, function=10, module=1, struct=3, type=2, variable=16 |
| fsharp | class=4, enum_member=2, field=3, function=5, method=6, module=1, property=1, struct=1, type=1, union=1, variable=18 |
| gdscript | class=4, constructor=1, event=1, field=2, method=9, variable=18 |
| go | field=1, function=6, import=1, method=2, module=1, struct=3, variable=15 |
| html | class=7, field=1, function=1, namespace=2, variable=1 |
| java | class=2, constant=1, constructor=2, interface=1, method=16, property=2, variable=23 |
| javascript | class=1, constructor=1, export=1, function=2, method=1, variable=6 |
| json | module=2, variable=4 |
| jsx | class=1, constructor=1, export=2, function=4, variable=6 |
| kotlin | class=2, constructor=1, function=1, interface=1, method=9, namespace=1, property=4, type=1, variable=10 |
| lua | class=2, field=2, function=3, import=1, method=3, variable=13 |
| markdown | import=2, module=2, property=2 |
| php | class=4, constant=1, constructor=2, function=7, import=1, method=1, namespace=1, property=1, trait=1, variable=15 |
| powershell | class=2, constructor=2, function=7, method=2, property=2, variable=19 |
| python | class=1, constructor=1, function=6, import=2, method=2, property=1, variable=12 |
| qml | class=1, event=1, function=10, import=1, property=8, variable=16 |
| qmldir | class=3, module=1 |
| r | class=1, field=1, function=3, import=1, method=2, variable=12 |
| razor | class=2, field=1, import=1, method=8, property=1, variable=11 |
| regex | class=1, function=3, variable=1 |
| ruby | class=2, constant=2, constructor=1, field=1, import=2, method=6, variable=16 |
| rust | field=1, function=8, import=1, method=1, namespace=1, struct=1, variable=10 |
| scala | class=6, constructor=1, function=3, method=13, namespace=1, property=4, trait=1, type=1, variable=19 |
| sql | class=2, field=4, interface=4, method=1, property=1 |
| swift | class=4, constructor=1, enum=1, enum_member=2, function=7, interface=1, method=6, module=2, property=4, struct=1, type=1, variable=18 |
| toml | module=2, property=8 |
| tsx | class=1, constructor=1, export=2, function=5, property=1, type=1, variable=9 |
| typescript | class=1, constructor=1, export=2, function=2, interface=1, method=2, variable=6 |
| vbnet | class=1, event=1, field=1, interface=1, method=11, namespace=1, property=1, variable=20 |
| vue | class=1, function=5, import=2, property=7, variable=12 |
| xml | module=3, variable=3 |
| yaml | module=2, variable=5 |
| zig | constant=1, field=2, function=13, import=1, method=3, struct=2, variable=28 |

## Remaining boundary

The incompatible 2.39 pin was the original integration blocker. The approved 2.40.0 publication, adoption and passing installed-binary verification above close that dependency. Native process-death/PID-reuse/unknown-identity qualification remains explicitly unverified in Miller; producer J1 has separately scoped native and deterministic evidence. No uncertainty authorizes dropping reader protection or using a stale legacy artifact. No Miller marketplace release is part of this pin-adoption task.

## Workspace inventory

Miller main remains at `54dab498f2e7132aac12b94fd80fae6e30b903ad`, clean and locally ahead of origin by 13 commits. Julie main remains at `3b3e5b6f03b724448df9012bb75224e99ca68f5d`, clean and locally ahead by 24. Semantic-sidecar main is clean and locally ahead by five. These ahead counts reflect local tracking refs, not a fresh remote synchronization.

All listed Miller and Julie worktrees were statused. Existing unrelated untracked files remain untouched in Miller's `v1.26.0-mcp-dogfood` worktree, `.tools`, and Julie's `ct-language-audit-plan` worktree, two audit documents. Other listed worktrees were clean apart from this task's documentation before its final commit. No worktree was removed. Registry prune was previewed only, with 33 unrelated candidates and six unconfirmed linked-worktree removals; no prune was applied.
