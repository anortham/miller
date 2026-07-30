# Julie Extract 2.20.0 and Semantic Sidecar 0.1.0 Adoption

**Date:** 2026-07-30
**Status:** Implemented and verified

## Purpose

Move Miller from `julie-extract 2.19.0` to `2.20.0` and from
`julie-semantic-sidecar 0.1.0-rc.5` to stable `0.1.0`. Prove that Miller
consumes the extractor's stronger reference-resolution output and that the
stable sidecar package works through Miller's existing restore, runtime, and
broker paths.

## Upstream Facts

- `julie-extract 2.20.0` keeps SQLite schema 5, extract contract 4, and JSONL
  schema 4. Resolution metadata advances from 3 to 6.
- The extractor adds C# local and parameter symbols with receiver type facts,
  TypeScript and JavaScript static-type receiver resolution, and stricter
  fail-closed cross-file resolution.
- `julie-semantic-sidecar 0.1.0` preserves the
  `julie.embedding.sidecar` v1 protocol and RC5 broker behavior.
- The stable sidecar package advances its package manifest to schema 2, adds a
  required third-party license file, and preserves native Windows cancellation
  error codes.

## Decision

### Extractor

- Update `scripts/julie-pins.json`,
  `MillerExtractContract.PinnedJulieExtractVersion`, and direct version
  assertions together.
- Keep Miller's schema and extract-contract constants unchanged.
- Update test-fixture resolution metadata and JSON assertions from version 3
  to version 6. Do not make resolution version a global artifact gate: the
  extractor automatically re-resolves stale artifacts, and Miller intentionally
  accepts compatible artifacts independently of product version.
- Add a Scale test using the real pinned extractor. It must prove that C# typed
  local/parameter receivers and TypeScript/JavaScript static receivers reach
  Miller through its public reference-evidence reader. It must also assert the
  artifact's resolution metadata is version 6.
- Do not add a new MCP tool, special-case a language, or duplicate extractor
  resolution logic in Miller.

### Semantic sidecar

- Update `scripts/semantic-pins.json` to stable `0.1.0` and the four published
  archive digests.
- Keep Miller's sidecar wire protocol and broker client unchanged.
- Update current operational messages that still tell users to restore or
  build RC5.
- Verify both restore scripts accept the stable manifest schema and the added
  third-party license file without weakening exact manifest validation.
- Update current third-party notices to name the two new pinned versions and
  the complete sidecar runtime directory.

## Architecture Quality

**No Architecture Impact:** this is a compatible producer/runtime adoption.
Existing extractor, reference-evidence, semantic-session, broker, and package
verification interfaces remain unchanged. New behavior is proved through the
same reader and runtime surfaces Miller already exposes.

## Likely Files

- `scripts/julie-pins.json`
- `src/Miller.Indexing/MillerExtractContract.cs`
- `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- `tests/Miller.Tests/Indexing/LiveReferenceResolutionScaleTests.cs`
- `scripts/semantic-pins.json`
- `tests/Miller.Tests/Indexing/SemanticBrokerScaleTests.cs`
- `scripts/semantic-broker-soak.sh`
- `scripts/semantic-broker-soak.ps1`
- `scripts/Miller.SemanticBrokerProbe/Program.cs`
- `THIRD-PARTY-NOTICES.md`

The implementation may touch an additional existing contract test when live
verification identifies a version-coupled assertion. It must not expand into a
new public contract or release-preparation work.

## Verification

- Restore both pinned packages and prove their reported versions.
- Verify the stable sidecar package manifest before installation.
- Run the focused pin, reference, restore, and broker tests.
- Run `dotnet build Miller.slnx -c Release`.
- Run `scripts/test.sh`.
- Run `scripts/test.sh scale`.
- Run the semantic broker soak on the local platform.
- Run Miller impact analysis over the final working-tree diff and inspect all
  changed public or operational surfaces.

## Acceptance Criteria

- [x] All extractor pin and assertion surfaces report `2.20.0`.
- [x] All semantic-sidecar pin and assertion surfaces report `0.1.0`.
- [x] Published archive names and SHA-256 digests match both live releases.
- [x] Miller still gates schema 5, extract contract 4, report schema 3, and
      JSONL schema 4.
- [x] Resolution metadata defaults and emitted evidence report version 6.
- [x] A real-extractor Scale test proves the new C#/TS/JS reference behavior
      through Miller's reference-evidence reader.
- [x] Stable sidecar manifest schema 2 and its third-party license payload pass
      Miller's exact package verification.
- [x] Current operational guidance contains no RC5 restore instructions.
- [x] Current third-party notices match both pins and the packaged runtime
      layout.
- [x] Release build, fast tests, Scale tests, and semantic broker soak pass.
- [x] No new MCP tool, public output contract, or language-specific Miller
      resolution logic is introduced.
