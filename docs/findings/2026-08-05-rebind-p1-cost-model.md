# Rebind P1 — artifact portability is free, but the cost model refutes the premise — 2026-08-05

**Status:** P1 ground-truth measurement for the
[worktree delta-rebind program](../plans/2026-08-02-worktree-delta-rebind-program.md). P1's shape
decision needed the cost of the operation rebind actually performs. Measuring it produced a result
that changes the program's sequencing, so this doc records the evidence before the contract is
written.

**Headline:** the artifact is fully portable between roots — the rebind surface is one metadata row,
cheaper than P1 assumed. But rebind eliminates **extraction of unchanged files**, and extraction is
0.2–20% of a delta scan's cost. The dominant cost is **identifier resolution, which runs over the
whole artifact regardless of delta size**, and which a populated (rebound) artifact pays at ~3× the
rate a from-scratch build pays. On this repo a rebind saves 12% at one changed file and goes
negative past ~120–150 changed files.

## 1. The rebind surface is one row

Verified on this repo's live artifact (1,397 files, 121,183 symbols, 469,645 reference sites,
755 MiB) and in julie-extractors source at `main` a8dc664:

- Every path column in the artifact is **root-relative**. A scan of all twelve path-bearing tables
  (`symbols`, `identifiers`, `reference_sites`, `source_regions`, `structural_facts`,
  `relationships`, `literals`, `complexity_metrics`, `parse_diagnostics`, `revision_file_changes`,
  `pending_relationships`, `type_argument_usages`) returned **zero** absolute paths.
- IDs do not encode the root. `file_id = stable_id("file", [target.root_relative_path])`
  (`crates/julie-extract-cli/src/extraction.rs:229`, `:262`, `:295`); symbol IDs come from
  `stable_location_id(file_path, name, span)` where `file_path` is the root-relative path threaded
  from `extraction.rs:160` (`crates/julie-extractors/src/base/types.rs:408-421`).
- `artifact_metadata` holds 15 keys, exactly one of which is root-derived: `root_path`.

**Implication:** retargeting an artifact from root A to root B is a single-row update, not a
table-rewriting migration. This is *better* than the P1 lean assumed, and it holds for both candidate
shapes.

## 2. The cost model — measured

Method: APFS-cloned this repo's working tree to a scratch root, built a base artifact from scratch,
then for each row cloned the base (`cp -c`), modified N `.cs` files, and ran a whole-repo
`julie-extract scan`. julie-extract 2.25.0, `--jobs 4`, warm cache, M-series/APFS. Phase numbers are
julie-extract's own `profile.phases`.

| Operation | Total | resolution | extraction+spool | vs. rebuild |
|---|---:|---:|---:|---|
| **Full build from scratch** (bulk path) | **16.40 s** | 4.66 s | 4.32 s | — |
| Delta, 0 changed | 0.10 s | 0 | 32 ms | `no_change` early exit |
| Delta, 1 changed | 14.40 s | 13.97 s | 32 ms | 12% faster |
| Delta, 25 changed | 13.93 s | 10.01 s | 152 ms | 15% faster |
| Delta, 100 changed | 13.66 s | 7.63 s | 252 ms | 17% faster |
| Delta, 170 changed | 19.54 s | 7.72 s | 1.80 s | **19% slower** |
| Delta, 294 changed | 21.52 s | 8.15 s | 2.00 s | **31% slower** |
| Delta, 654 changed | 45.47 s | 18.26 s | 3.26 s | **177% slower** |

Read the second and third columns together: **a one-file delta spends 13.97 s resolving — 3× the
4.66 s the full rebuild spends resolving the identical corpus** — while the extraction work a rebind
eliminates is 32 ms of a 14.4 s scan.

Crossover between "delta wins" and "just rebuild" sits between 100 and 170 changed files, roughly
9–11% of this repo's indexed files. A real sibling-branch pair in this repo diverges by 723 indexed
files — past the crossover.

## 3. Why — two compounding causes, both in source

**Cause 1: whole-repo scans never scope resolution.** Both whole-repo write sites construct the
resolution scope with `is_full_scan: true` hard-coded
(`crates/julie-extract-artifact/src/writer.rs:1087`, `:1390`), and the CLI hook computes
`let effective_full = scope.is_full_scan || prior.is_none();`
(`crates/julie-extract-cli/src/resolution.rs:1551`), taking the whole-workspace locator and
covered-set branch. The single-file `update`/`delete` paths pass `is_full_scan: false`
(`writer.rs:679`, `:842`) and get the scoped branch — `delta_scope_files` +
`IdentifierLocator::load_scoped` (`resolution.rs:1561-1571`).

`is_full_scan` is a *label for the call site*, not a correctness requirement: the field's own doc
says it "is true for the whole-tree scan paths (`Full` scope) and false for single-file
update/delete" (`writer.rs:170-171`), and `changed_file_ids` is **already computed and passed** at
both whole-repo sites (`writer.rs:1085`, `:1388`) — it is simply ignored. The delta scope data the
scoped branch needs is present and discarded.

**Cause 2: a populated artifact can never take the bulk path.** `artifact_is_unwritten` requires zero
files *and* zero revisions (`writer.rs:1639-1646`), so only a from-scratch build gets
`journal_mode=MEMORY`, `synchronous=OFF`, `foreign_keys=OFF` and drop-and-rebuild-indexes-once. A
rebound artifact is populated by definition and is permanently ineligible — which is why its
resolution phase costs 3× the from-scratch build's.

## 4. Per-file `update` is not a workaround

A rebind could in principle skip the whole-repo scan and issue one `update` per changed file, since
the delta set is knowable from git at negligible cost (`git diff --name-status` is 0.02–0.04 s at any
tree size). Measured, it does not batch:

| Path, same 25-file change | Wall |
|---|---:|
| One whole-repo `scan` | 13.93 s |
| 25 sequential `update` calls | 64.76 s (2.59 s/file) |

Each `update` reloads the whole-workspace candidate index — `load_index(tx)` is whole-workspace on
every invocation regardless of scope, and says so (`resolution.rs:1553-1556`). Above a handful of
files, per-file updates lose to the whole-repo scan they were meant to avoid.

## 5. Base+overlay is refuted, not deferred

The P1 plan carries base+overlay (SQLite `ATTACH` read-through to the main artifact + a per-worktree
overlay of changed files) as "the possible future shape." It is not viable, on correctness rather
than cost: `stable_location_id` folds the full 8-field span into the ID input
(`crates/julie-extractors/src/base/types.rs:408-421`), so **any edit that shifts a byte offset
re-IDs every symbol below it in the file**. An adversarial pass reproduced the consequence with the
pinned 2.25.0 — adding one comment line changed a symbol ID from `41db6edd…` to `17a79bf4…`, and the
base∪overlay union then returned zero references where the base alone returned the correct caller.
Miller cannot even detect the loss: references whose target symbol row is absent are filtered out
(`src/Miller.Indexing/SqliteSymbolGraphIndex.cs:529-542`).

Note the asymmetry that makes this easy to get wrong: §1's portability facts say IDs are stable
across **root moves**, which is what rebind needs. They say nothing about stability across **content
edits**, which is what overlay needs — and there they are unstable by construction.

**Recommendation: remove base+overlay from the program plan rather than keeping it documented as a
future option.** Leaving it in invites a later revival of a design whose central claim is disproven.

## 6. What this means for P1

The mechanism P1 was going to specify is sound and cheaper than assumed. The *value* it delivers is
not there yet: rebind attacks extraction, and extraction is not the cost. With resolution scoping
unfixed, a rebind contract ships a 12–17% improvement with a crossover at ~10% file divergence —
below which it barely pays, above which it is worse than the full rebuild it replaces.

The prerequisite that makes rebind pay is **honoring the already-computed delta scope on whole-repo
scans**. That change is independently valuable: it is on the path of every whole-repo delta Miller
runs — session-start bootstrap reconcile, `workspace refresh`, cross-workspace refresh-first reads —
on every workspace, not only worktrees. Miller's watcher path already routes per-file work to the
cheap scoped verbs (`src/Miller.Core/Freshness/WatchEventRouter.cs:80`), so this is specifically the
owed-whole-repo-scan path.

## 7. Limits of this measurement

- **Scale is unmeasured and extrapolation is unfavourable.** The program targets 74k files. The
  dominant cost here scales with **artifact** size — the same axis the full build scales on, with
  worse constants — so the gap is expected to widen, not close. But that is inference: no delta-path
  measurement exists at 74k, and the scan targets are gone from disk (W10 fixture never checked in;
  the dotnet/runtime clone deleted — see
  [the P0 audit](2026-08-05-rebind-p0-measurement-audit.md)).
- **One repo, one platform.** 1,397 files, C#-dominant, macOS/APFS. Repos with different
  identifier density will sit elsewhere on the curve.
- Run-to-run variance on the 1-file delta was 10.8–14.4 s across two base artifacts; the table is one
  consistent series against a single base.
- The 4.05× savepoint figure quoted in the dotnet/runtime baseline was measured on the **bulk** path
  and attributed to `journal_mode=MEMORY` (`writer.rs:1553-1557`); it does not describe the WAL delta
  path measured here. The 3× delta-vs-bulk resolution penalty above is this doc's own measurement.
- The P0 audit's "clonefile is free" result was measured with `cp -c` on a **quiescent** artifact. A
  live source artifact under its own leader has no order-safe quiescence protocol
  (`src/Miller.Indexing/ScanGovernor.cs:79-82` forbids taking a second workspace's writer lock while
  holding the lease), so a safe copy is either a coordinated snapshot or a full byte copy. That
  finding needs an amendment before it is cited as the shape justification again.

## Verdict

P1 should not freeze a rebind contract on today's julie-extract. The contract's *mechanism* is
settled and cheap (one metadata row, no path rewriting, no ID re-derivation). Its *payoff* is gated
on delta-scoped resolution for whole-repo scans, which is a julie-extractors change that pays for
itself independently of the worktree program.
