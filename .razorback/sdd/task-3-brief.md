### Task 3: RebindEligibility pure decisions

**Files:**
- Create: `src/Miller.Indexing/RebindEligibility.cs`
- Test: `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs`

**Interfaces:**
- Consumes: `LeadershipEligibility`'s numeric `major.minor.patch` comparison
  (`src/Miller.Indexing/LeadershipEligibility.cs` — reuse/extract its version-triple parser rather
  than duplicating), `ArtifactRootIdentity.Matches`, `IndexLevels.ResolveForWorkspace` semantics,
  `MillerExtractContract.PinnedJulieExtractVersion`.
- Produces: pure statics Task 6 calls, split in two stages —
  `RebindPrefilter.Evaluate(RebindPrefilterInputs) → RebindDecision` (registry-level, cheap,
  provisional) and `RebindSnapshotValidation.Evaluate(RebindSnapshotInputs) → RebindDecision`
  (authoritative, against the copied `.rebuild`). `RebindDecision` carries eligible/ineligible + a
  human-readable reason string (surfaced in logs/status). Inputs are plain records (bools,
  strings, versions) — NO I/O in this file; callers gather facts.

**Contract inputs:** contract design §6, all eight numbered conditions. Prefilters: linked
worktree + `!dbExists` + no replacement (Task 2's fold) + registered main-checkout sibling with an
existing `symbols.db` + numeric-triple pin equality + NO standing W8 failure record (any record —
conservative, §7.4) + `MILLER_FULL_REBUILD_INPLACE` unset + `MILLER_WORKTREE_REBIND` not `off`
(env read happens in the caller; the pure input is a bool). Snapshot validation: schema/contract
compatible + `hash_algorithm = blake3` + recorded `root_path` matches the SOURCE root + at least
one committed extraction revision (`ServableFor` alone is NOT sufficient — crash shells pass it) +
`binary_version` numeric equality re-check + recorded `index_level` satisfies the target's
resolved level policy (full satisfies all; symbols satisfies SymbolsOnly/Progressive but NOT
Full). Level changes require a fresh force rebuild, never a rebind.

**File ownership:** Create `src/Miller.Indexing/RebindEligibility.cs`; Test `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Every go/no-go decision in the rebind path as I/O-free, fast-suite-testable
statics, in the `LeadershipEligibility` style. This is the P3 acceptance item "eligibility as
pure, fast-suite-testable decisions".

**Approach:** One test per condition per stage, plus the crash-shell case: a snapshot input with
`hasCommittedRevision: false` and everything else valid is ineligible with a reason naming the
missing committed revision. If `LeadershipEligibility`'s triple parser is private, extract it to a
shared internal helper (do not change its public behavior).

**Acceptance criteria:**
- [ ] Each §6 condition flips the decision independently (table-driven tests, both stages).
- [ ] Crash-shell (no committed revision) is ineligible at snapshot validation even though
      `ServableFor`-style facts pass.
- [ ] Version comparison uses the numeric triple, proven by a case raw string equality would get
      wrong (e.g. `2.27.0` vs `v2.27.0` spelling divergence).
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

