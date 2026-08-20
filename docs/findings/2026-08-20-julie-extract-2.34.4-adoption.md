# julie-extract 2.34.4 pin adoption

- **Pin moved:** `2.34.1` → `2.34.4`.
- **Upstream:** [`anortham/julie-extractors` v2.34.4](https://github.com/anortham/julie-extractors/releases/tag/v2.34.4).
- **Tag provenance:** upstream tag `v2.34.4`, four published assets.
- **Release state:** GitHub reports the release as Latest, published `2026-08-20T17:14:32Z`.

## What changed

This pin skips two intermediate releases, so it carries three upstream changes.
All three change test-role detection in the producer. None changes a contract.

- **2.34.2** promotes `test_container` and `test_lifecycle` on QML, GDScript,
  Bash, and Scala symbols.
- **2.34.3** narrows test detection for Python, Scala, and Elixir.
  `pytest.fixture` and `unittest.mock.*` stop counting as test evidence, and a
  bare `test_` name now needs test-path evidence too.
- **2.34.4** adds Windows test hardening and closes the test-role work.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.34.4-aarch64-apple-darwin.tar.gz` | `0284de63b9f15b3aa546e234d40e1949cf88076415ab17c8842e1d5e76a0843b` |
| `x86_64-apple-darwin` | `julie-extract-v2.34.4-x86_64-apple-darwin.tar.gz` | `f8a4a00319dc43a62a3116ad130df652823be8affdd5a003614c3404bbd7a23c` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.34.4-x86_64-pc-windows-msvc.zip` | `57f93f95165fdc5c0472c36fcf8864f27e758a4a2423d421efc769061708e86a` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.34.4-x86_64-unknown-linux-gnu.tar.gz` | `eb0aecba3963f246a2d2e05325d8536cbd585e03014e8e34356b16e9db078af8` |

Each value was verified against the live GitHub release, then verified again by
the restore script after download.

## Measured contract compatibility

A probe fixture was scanned with the restored 2.34.4 binary. The binary reported
these values, and every one matches the constant Miller already gates on in
`src/Miller.Indexing/MillerExtractContract.cs`.

| Reported constant | Value |
| --- | --- |
| artifact schema | `7` |
| SQLite schema | `7` |
| extract contract | `4` |
| report schema | `3` |
| JSONL schema | `5` |
| hash algorithm | `blake3` |

No contract moved, so only the pin string changes. Miller needs no schema,
migration, parser, or MCP-tool change.

## Measured consumer impact

The three upstream releases all change which symbols the producer marks as test
code, so the risk is a changed column value, not a changed schema. The check was
a producer diff on real sources, not a reading of the release notes.

**Method.** Extract this repository's Python and bash sources with the restored
2.34.4 binary. Diff the result against the live 2.34.1 store on the three
test-role columns `is_test`, `test_container`, and `test_lifecycle`.

**Result.** 5,866 common symbols. Zero differences on all three columns. All 376
bash symbols carry zero for all three roles, before and after.

Miller reads `is_test`. It reads neither `test_container` nor `test_lifecycle`
anywhere. Four consumers read the bare `is_test` flag with no path fallback, so
a producer change in test detection reaches them first:

- `src/Miller.Indexing/ComplexityRankingReader.cs:56` and `:151`
- `src/Miller.Indexing/ImpactAnalysis.cs:80` and `:144`

The producer diff shows no change at those four sites for this repository's
sources.

## Verification

- The four SHA-256 values were checked against the live release and re-checked
  by the restore script.
- Debug build: 0 warnings / 0 errors. The `VerifyPinnedJulieExtractVersion`
  build guard passes against the restored binary.
- Fast suite: 7,548 passed / 1 failed / 27 skipped / 7,576 total. The one
  failure was
  `SharedSemanticBrokerConnectionFactoryTests.PassiveObservation_DisposesAConnectedStreamCanceledBeforeSessionAcceptance`.
  It passed on a focused re-run. It fails only under parallel-agent load, so it
  is a load-sensitive test, not a pin regression.
- Release compiles clean. It fails only when it copies DLLs into the running
  server's output folder.

## Standing note for the next pin bump

Prove safety with a producer diff on real sources. Do not argue from consumer
robustness.

An argument that Miller ignores a column, or that a consumer tolerates either
value, predicts nothing about what the new binary actually writes. Extract real
sources with the new binary, diff the changed columns against the live store,
and record the common-symbol count and the difference count. That is what this
finding does above, and it is what makes the compatibility claim checkable.
