# julie-extract 2.15.0 Adoption Design

## Goal

Upgrade Miller's bundled `julie-extract` from 2.14.0 to 2.15.0 and consume the
new extractor output that affects Miller behavior.

## Architecture

The artifact contract remains schema version 4, extract contract version 3,
and report schema version 3, so the upgrade needs no database migration.
Miller keeps its existing generic structural-fact and test-role readers.

`symfony.route.v1` and `ktor.route.v1` enter the existing backend HTTP bridge
whitelists. They use the existing normalized/effective route-template and
optional verb adapter contract; no family-specific provider logic is added.

The existing pure extraction-report warning helper expands from partial-only
reports to any report with operator-visible diagnostics. Partial artifacts keep
their current warning text. Successful reports with `warnings`, including
`slow_file_skipped`, produce a warning that names codes and affected paths.
Every existing scan caller uses the generalized result.

## Current-Surface Updates

- Pin all four published 2.15.0 archives by their verified SHA-256 digests.
- Update current pattern-catalog guidance from 175 to 194 IDs across 36 languages.
- Document Symfony and Ktor in the current backend HTTP bridge family lists.
- Preserve historical release notes and findings as historical evidence.
- Add no MCP tool and no new artifact contract.

## Verification

- Unit tests prove both new route IDs enter the load and route whitelists and
  produce backend HTTP bridge matches through the existing adapter/provider.
- SQLite-reader tests prove the new facts survive Miller's load whitelist.
- Live Scale extraction proves v2.15.0 emits Symfony/Ktor facts and Miller
  builds the expected bridges; the parity gate covers all 30 backend families.
- Live Scale extraction proves Kotlin JUnit container/lifecycle roles round-trip
  through existing role consumers.
- Warning tests prove successful `slow_file_skipped` reports are surfaced while
  partial-report behavior stays stable.
- The restored binary reports 2.15.0; Release build, fast tests, Scale tests,
  generated guidance mirrors, and plugin manifest tests pass.

## Acceptance Criteria

- [ ] Miller pins and restores `julie-extract` 2.15.0 for all supported targets.
- [ ] Symfony and Ktor routes participate in backend HTTP trace matching.
- [ ] Successful extractor warnings are visible to operators at every scan path.
- [ ] Current catalog and bridge guidance reflects 194 IDs and the new families.
- [ ] Contract versions remain unchanged and no migration or MCP tool is added.
- [ ] All focused and repository-defined verification gates pass.
