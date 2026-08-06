### Task 1: Registry lineage columns + sibling lookup

**Files:**
- Modify: `src/Miller.Indexing/WorkspaceRegistry.cs` (schema head near :8-21, `UpsertSeen`
  :77-123, the duplicate-column-tolerant `ALTER TABLE ADD COLUMN` migration pattern used for
  `level_policy` around :337-370, `PruneDuplicatePathRowsUnderLock` untouched)
- Test: `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`

**Interfaces:**
- Consumes: `GitWorktreeLayout` (`GitDir`, `CommonDir`, `MainCheckoutRoot`, `IsLinkedWorktree` —
  `src/Miller.Indexing/GitWorktreeLayout.cs:32-39`), `WorkspaceRootIdentity`
  (`src/Miller.Indexing/WorkspaceRootIdentity.cs:27`), `PathCanonicalizer`.
- Produces: four nullable columns on `workspaces` — `git_common_dir TEXT` (canonicalized),
  `git_is_linked INTEGER`, `git_dir TEXT`, `git_dir_created_at TEXT` (ISO-8601 round-trip) —
  surfaced as nullable members on `WorkspaceRegistryRow` (`GitCommonDir`, `GitIsLinked`, `GitDir`,
  `GitDirCreatedAtUtc`); an `UpsertSeen` signature extended with an optional lineage argument
  (single record parameter `WorkspaceLineage?` preferred over four positionals); a query
  `WorkspaceRegistryRow? FindMainCheckoutByCommonDir(string canonicalCommonDir)` returning the
  non-linked row whose `git_common_dir` matches, or null.

**Contract inputs:** contract design §5. `git_common_dir` MUST be canonicalized through
`PathCanonicalizer` before storage (raw `GetFullPath` strings silently miss on macOS
`/var`→`/private/var`). Columns are additive and nullable — invisible to older Millers. A null
lineage argument leaves existing stored lineage untouched (an upsert from a context without git
resolution must not erase identity another process persisted).

**File ownership:** Modify `src/Miller.Indexing/WorkspaceRegistry.cs`; Test `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The registry persistence for repository lineage: which repo family a workspace
belongs to (`git_common_dir`), whether it is the main checkout or a linked worktree, and both
halves of the checkout-generation identity so path-reuse detection survives restarts.

**Approach:** Follow the existing `level_policy` migration one-off but generalize it into a small
loop over (column, type) pairs so the four new columns and future additions share one
duplicate-column-tolerant path. Store timestamps ISO-8601 like the existing `*_at` columns. Lookup
matches with `ArtifactRootIdentity.ComparisonFor` semantics (case-insensitivity on
Windows/macOS) — compare via SQL `COLLATE NOCASE` only if the existing registry already does so
for paths; otherwise filter in C# for consistency with `ArtifactRootIdentity.Matches`.

**Acceptance criteria:**
- [ ] Lineage columns migrate on an existing registry DB (fixture with the old schema opens
      cleanly and reads null lineage), and round-trip values exactly, including the
      creation-timestamp half.
- [ ] `FindMainCheckoutByCommonDir` returns the main-checkout row among mixed rows, ignores linked
      rows and other repos, and applies platform path-comparison semantics.
- [ ] Null-lineage upsert preserves previously stored lineage.
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

