## Task 6: Smoke the exact packaged semantic payload

**Depends on:** Task 2 session contract.

**Owns:**

- `.github/workflows/release.yml`
- a new cross-platform smoke script and its tests, if a script keeps YAML small
- release-process/package documentation if the contract changes

**Red contract test:** packaged smoke fails when sqlite-vec is missing, sidecar identity is wrong, embedding dimension mismatches, or KNN cannot return the inserted vector.

**Implementation:**

- Run the smoke against the staged archive contents for every RID before archive upload.
- Load the staged sqlite-vec extension, launch the staged semantic sidecar with Miller's active pin, embed one fixed input, insert/query one vector, and verify identity/dimension/result.
- Keep download/network work outside the smoke; the package must already be self-contained.

**Worker verification:** script contract tests plus local-host smoke for the current RID. Other RIDs are verified by workflow structure and the next package-only run; do not dispatch a workflow or publish.

