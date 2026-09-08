# julie-extract 2.41.1 pin adoption

Miller now pins the public [julie-extract 2.41.1 release](https://github.com/anortham/julie-extractors/releases/tag/v2.41.1).
The immutable tag resolves to `25435850fb0ffed277ead9727ebc9e9ebd66ee41`; the producer's evidence closeout is `28d65be9`.
This is a source pin adoption. The published Miller 1.28.1 packages still contain 2.40.6.

## Published assets

| Target | SHA-256 |
| --- | --- |
| `aarch64-apple-darwin` | `a6bfabd12f156384c4b97d348eb8941c728ec014fac80308bac0b3699cc26647` |
| `x86_64-apple-darwin` | `465c984ccdd41d66d925d756a2bec2b2d19a7b564aa168f62ff7770f0d2c0992` |
| `x86_64-pc-windows-msvc` | `a418609d24deda41566fce8b37a0be616c482ff3b546186ca5eb4cd0b44faca1` |
| `x86_64-unknown-linux-gnu` | `0dc93f6871dbf77163eb09f69e11dbcf0bfeed8a5222e2768bd3a3e5a09dbf3e` |

All four public downloads matched GitHub's digests and their embedded checksums. Packaged notes matched the producer source, and the release body matched the packaged notes. Miller's restore script independently verified the Linux archive and installed a binary with SHA-256 `d157d9465902eb1321c410af4149c004239e0cdecfd06d036e74966342de9465` that reports `julie-extract 2.41.1`.

## Compatibility and changed facts

The SQLite artifact schema remains 7, extract contract 4, report schema 3, JSONL schema 5 and hash algorithm `blake3`. The family store remains schema 2 and format epoch 1. Extraction identity advances from epoch 9 to 10 because existing files need re-extraction to receive the new facts.

The release adds exact return-type usages for all 25 natively applicable languages, ASP.NET `MapHead`, `MapOptions` and multi-verb `MapMethods` facts, consumed htmx/component expressions, and Java namespace facts for a single-segment package. Fifteen languages without native return syntax remain explicitly N/A.

Fresh 2.41.1 families stamp reader and writer floors at 2.41.1. Miller's independently qualified family reader therefore advances to 2.41.1 and continues to reject unqualified 2.41.2 and 2.42.0 floors. Existing families retain their prior minimum reader while moving to extraction epoch 10.

## Native adoption evidence

A published 2.40.6 binary imported an unchanged Java fixture at epoch 9 without a namespace fact. The byte-identical 2.41.1 workflow/public Linux binary imported the same source as a new manifest at epoch 10 and added `namespace sample`. A private Miller runtime then refreshed both owned sidecars and returned the exact `package sample` namespace through `search` and `inspect` at the current revision.

The same 2.41.1 Linux binary passed the real 40-language new-family reader test and the native Gradle single-package selection test. The Java control failed with 2.40.6 because the missing namespace made the native binding unknown; with 2.41.1 it selected the requested test and excluded the unrelated failing class.

Evidence is under `/home/murphy/.cache/miller-dogfood/adoption-2.41.1/`:

- `workflow-linux-all40-new-family.log`
- `workflow-linux-java-single-package.log`
- `workflow-epoch9-import.json`, `workflow-epoch10-import.json`, `workflow-epoch-upgrade-sql.txt`
- `workflow-miller-open.json`, `workflow-miller-status.json`, `workflow-miller-search.json`, `workflow-miller-inspect.json`
- `miller-restore-public.log`, `public-contract-scan.json`, `public-contract-info.json`

## Miller verification

- Pin, schema, reader-floor and capabilities scope: 46 passed, zero failed.
- `dotnet build Miller.slnx -c Release --no-restore`: zero warnings and errors.
- Fast suite: 10,423 passed, nine skipped, zero failed.
- Scale suite: 233 passed, 38 skipped, zero failed. Host-unavailable native providers retain their separately recorded container/toolchain proofs.
- The root and Release-output extractor binaries are byte-identical and report 2.41.1. `miller capabilities --json` reports pin 2.41.1 with the unchanged contract values.

Logs and TRX files are under `/home/murphy/.cache/miller-dogfood/adoption-2.41.1/`. Plugin guidance files did not change during adoption, so the prior 54-test plugin result remains the applicable gate. Miller was not released or pushed by this adoption.
