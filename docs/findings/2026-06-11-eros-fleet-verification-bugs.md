# 2026-06-11 — Eros fleet-verification findings (Miller v0.4.0+44af8e6)

Found while verifying the `cli-eros-v1` contract against real workspaces from the Eros design
session. All three are reproducible on `miller 0.4.0+44af8e6ac260` with `julie-extract` pin 2.4.0,
run from cwd `/Users/murphy/source/eros` against the registered workspace
`/Users/murphy/source/goldfish` (whose `symbols.db` was on extract schema 2).

## Bug 1 — `workspace full` reports success but does not heal a schema-mismatched DB

**Severity: high.** False-healthy lifecycle state.

Repro:

1. Goldfish workspace has a schema-2 `symbols.db`; build expects schema 3. `patterns` correctly
   fails with: *"rebuild the index with `workspace full` (a force rebuild)"*.
2. Run `workspace full` against goldfish (via MCP `workspace` tool, `workspace_id` path selector —
   correctly targeted). Result: `status: unchanged, scanned: yes, swapped: no, revision: 1`.
3. `patterns` against goldfish still fails with the same schema-2 error.

The force rebuild scanned, concluded "unchanged" (file hashes match), never re-extracted, never
swapped in a schema-3 DB. The error message's own prescribed remedy does not work; the failure
loops. Expected: `workspace full` must treat a schema-version mismatch as "everything changed" and
unconditionally re-extract + swap. The bundled binary IS 2.4.0 (capabilities confirms the pin), so
the "restore first" branch does not apply.

## Bug 2 — CLI `workspace status|full` silently ignore `--workspace` and `--workspace-id`

**Severity: high.** Wrong-target operations.

Repro (cwd = `/Users/murphy/source/eros`):

```
miller workspace status --workspace /Users/murphy/source/goldfish --json
miller workspace status --workspace-id /Users/murphy/source/goldfish --json
miller workspace full --workspace /Users/murphy/source/goldfish --json
```

All three resolve to the **eros** workspace (`root: /Users/murphy/source/eros` in the response);
the selector is silently dropped. The `full` case attempted a force rebuild of the *wrong*
workspace (it hit `lock_busy` on eros). Goldfish is registered, and the same path selector resolves
correctly through `patterns --workspace ...` and through the MCP `workspace` tool — the bug is
specific to the CLI `workspace` subcommand family. `miller workspace --help` advertises
"the current (or a selected) workspace".

Expected: selectors honored, or exit 2 (usage error) if a subcommand does not accept them. Silent
fallback to cwd is the worst outcome for a fleet orchestrator: Eros calling
`workspace full --workspace <repo>` per repo would rebuild the caller's cwd workspace N times and
report fleet convergence that never happened.

## Bug 3 — Schema-mismatch failures exit 1, contract says 3

**Severity: low.** `cli-eros-v1` defines exit 3 as "operational failure such as no usable index".
`miller patterns summary --workspace /Users/murphy/source/goldfish --json` against the schema-2 DB
prints a clean one-line error but exits **1** ("unexpected failure") instead of **3**. Eros-side
ingestion wants to branch on 3 (queue a rebuild) vs 1 (bug, alert); today both look like 1.

## Resolution (2026-06-11, same day)

All three bugs are fixed and covered by fast-suite tests; verified end-to-end against a real
schema-2 artifact (downgrade → `patterns` exit 3 → cross-cwd `workspace full --workspace` →
schema 3, `status: refreshed` → `patterns` exit 0).

- **Bug 1** decomposed into three Miller-side root causes — julie-extract 2.4.0 itself heals
  correctly (`scan --force --strict-schema` deletes and recreates an incompatible DB; verified in
  the v2.4.0 tag and against the live goldfish artifact, which the original repro had in fact
  already healed at 17:07:23Z):
  1. `SqliteReadOnlyAccess.Open` left SQLite connection POOLING on (every other reader sets
     `Pooling=false`). The rebuild unlinks+recreates `symbols.db`, so pooled handles in a live
     server pinned the old inode and every later read — including the error's own retry — kept
     seeing the schema-2 database. Fixed with `Pooling=false`
     ([`SqliteReadOnlyAccess.cs`](../../src/Miller.Indexing/SqliteReadOnlyAccess.cs);
     `ContentCorpusContextReader` had the same gap).
  2. `CrossWorkspaceRefreshService` judged refreshed-vs-unchanged by `revision > LastRevision`; a
     from-scratch rebuild restarts the revision counter (1 == old 1), so the successful rebuild was
     reported `unchanged`. Now judged by the julie report itself (`status=="no_change"`).
  3. `WorkspaceIndexProvider` caches are keyed `(workspace, db, revision)`; a rebuild landing on
     the same revision collided with pre-rebuild entries. A Refreshed result that does not advance
     the revision now evicts the workspace's cache entries.
- **Bug 2**: the CLI `workspace` subcommand family parsed only `--id`/`--path` and silently dropped
  `--workspace`/`--workspace-id`. Both aliases are now accepted (path alias normalized against the
  CLI cwd), a valueless selector flag is exit 2, and the help text + `cli-eros-v1` document the
  selector parity.
- **Bug 3**: `IncompatibleExtractException` derives directly from `Exception`, so it fell through
  every verb's exit-3 catch into the generic exit-1 handler. `CliDispatch.Run` now maps it to
  exit 3 for all verbs.

## Contract-coverage note (not a bug)

While verifying dependency-inventory feasibility: `json.property.v1` facts cover `package.json`
(e.g. `--where key=axios` returns hits with `metadata.path: "$.dependencies"`), and
`toml.key_value.v1` / `yaml.key_value.v1` exist for pyproject/yaml manifests. There are **no XML
structural facts** — `.csproj` `PackageReference` is invisible to `patterns`, which matters for
.NET-heavy fleets. Candidate julie-extractors addition: `xml.element.v1` / msbuild-aware pattern.
