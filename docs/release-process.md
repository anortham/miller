# Miller Release Process

Use a two-step release path so publishing does not rebuild the full platform matrix.

## 0. Windows Verification (required before every release)

CI runs no per-push Windows test job (hosted runners were slow and flaky). Windows proof comes from
the local win-test KVM guest instead, and it is a REQUIRED gate before package validation:

```bash
win-test up            # if the guest is shut off
win-test sync miller   # refuses a dirty host tree; syncs HEAD
win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"
```

The suite must report 0 failures on the release commit (or the source-final commit when later
commits are docs-only). Record the result in the release verification finding. The win-test skill
(`~/.claude/skills/win-test`) documents the guest; `scripts/test.ps1` mirrors the wrapper for runs
inside the guest.

## 1. Validate Packages

Run the release workflow with publication disabled:

```bash
gh workflow run release.yml \
  -f version=<version> \
  -f prerelease=false \
  -f publish=false \
  -f allow_overwrite=false
```

Wait for the run to finish successfully. This builds, packages, checksum-generates, uploads, and smoke-tests
the platform archives.

## 2. Promote The Validated Run

Promote the successful package-only run by ID:

```bash
gh workflow run release.yml \
  -f version=<version> \
  -f prerelease=false \
  -f publish=true \
  -f allow_overwrite=false \
  -f promote_run_id=<successful-package-run-id>
```

The promote path downloads the existing artifacts, verifies all archive checksums, and creates the GitHub
release without rebuilding the platform matrix.

For local promotion with the same checks:

```bash
scripts/release-promote.sh --version <version> --run-id <successful-package-run-id>
```

Use `--dry-run` to verify artifacts without creating or updating a release.

## Pinned Binaries (julie-extract and the semantic pair)

Every platform archive carries two pinned toolsets under `.tools/`:

| Pin file | Restored artifacts | Restore script |
| --- | --- | --- |
| `scripts/julie-pins.json` | `julie-extract[.exe]` | `scripts/restore-julie-extract.sh` / `.ps1` |
| `scripts/semantic-pins.json` | `julie-semantic-sidecar-runtime/`, `vec0.dylib`/`vec0.so`/`vec0.dll` | `scripts/restore-semantic-sidecar.sh` / `.ps1` |

The release workflow restores both by pin — it never hardcodes URLs or checksums — and each runner's host
platform equals its matrix target, so the scripts' host detection resolves the right asset. The semantic
restore verifies every file, role, size, and checksum in `julie-semantic-sidecar-runtime/package-manifest.json`
before atomically installing the complete runtime directory. Before either archive step, every RID runs
`Miller.PackageSemanticSmoke` against that leg's exact
`artifacts/publish/<target>` staging directory. The helper launches the staged sidecar with Miller's active
encoder pin, embeds one fixed query, loads the staged sqlite-vec extension, inserts that emitted vector into
`vec0`, and requires a KNN self-query to return the inserted row at near-zero distance.

Model acquisition is a separate setup step before the smoke. The smoke itself never downloads and fails if
the active model is not already prepared. Release packages remain self-contained for executable payloads;
model weights keep the product's explicit, consented shared-cache lifecycle through `miller semantic prepare`.
The sqlite-vec member name is read from `scripts/semantic-pins.json` per RID rather than assumed.

Semantic restore failure fails the release job even though semantic retrieval is optional at runtime
(ADR-0003): a local build with no restore is fine, but a published archive silently missing the sidecar is
not.

To bump the semantic pin:

1. Update `version` and every `sha256` under `sidecar` (four triples) in `scripts/semantic-pins.json`;
   update the `sqliteVec` block the same way if the extension version moves.
2. Re-run `scripts/restore-semantic-sidecar.sh` (or `.ps1` on Windows) locally. The
   `VerifyPinnedSemanticSidecarVersion` build guard in `src/Miller.Server/Miller.Server.csproj` fails the
   build if `.tools/` still holds the pre-bump sidecar.
3. Run `scripts/test.sh scale` — the real-sidecar Scale tests assert the handshake's `encoder_fingerprint`
   matches `MillerSemanticContract.DefaultEncoder`. A fingerprint change is a `vectors.db` generation
   change and must be called out in the release notes.

## Release Notes (required for every release)

Every release — feature or patch — ships release notes in both places:

1. Write `docs/release-notes/v<version>.md` following the existing format (audience, release shape,
   pinned extractor, What Changed, Upgrade Notes, Verification), and update the `docs/README.md` map
   ("latest release notes" pointer moves to the new file; the previous latest becomes historical).
2. Set the same content as the GitHub release body:

   ```bash
   gh release edit v<version> --notes-file docs/release-notes/v<version>.md
   ```

The publish workflow creates the release with a placeholder body; a release is not done until the notes
file exists and the release body carries it. (v1.10.0 and v1.11.0 shipped without notes and were
backfilled on 2026-07-17 — do not repeat that.)

### Required notes for the first release that ships the semantic pair

- **`encoder_fingerprint` changed.** `ModelRevision` was corrected to the Hugging Face repo revision
  `main` rather than the gguf file name (commit `f68dad8`). Any pre-existing local `vectors.db` therefore
  reclassifies as `incompatible` and rebuilds automatically on first use — no user action, but say so.
- **Semantic stays optional and off-switchable.** `MILLER_SEMANTIC=off` remains a permanent zero-work
  guarantee and lexical-only output stays byte-identical (ADR-0003). A workspace that never enables
  semantic pays nothing for the two extra packaged binaries.

## Guardrails

- Do not publish from a failed, cancelled, or in-progress package run.
- The semantic packaging change touches `.github/workflows/release.yml`, so the CLAUDE.md tag-push 403
  rule applies: do not cut a tag-push release from a commit whose workflow diff against default-branch
  HEAD is unmerged, and do not push further workflow-touching commits to main while a tag-push release run
  is still building. Recover a 403'd publish job with `scripts/release-promote.sh` per that rule.
- Do not overwrite an existing stable release unless explicitly intended; pass `allow_overwrite=true` or
  `--allow-overwrite` only for that case.
- Keep `Directory.Build.props`, `miller-plugin.json`, `.claude-plugin/plugin.json`,
  `.cursor-plugin/plugin.json`, `.codex-plugin/plugin.json`, `.claude-plugin/marketplace.json`, and release notes
  in sync before package validation.
- When plugin manifests or `bin/miller-plugin-launcher.cjs` change, run `scripts/test-plugin.sh` and a
  Cursor-style smoke from `.cursor-plugin/plugin.json` against a local `MILLER_BINARY` to confirm `initialize` and
  `tools/list` complete. Run one smoke from `/` with no workspace env to prove Cursor's no-folder Settings launch
  does not fail, and one smoke from a non-Miller repo cwd to prove project launches still bind to that repo.
  Expand `${CURSOR_PLUGIN_ROOT}` to the package root in the smoke; Cursor rejects local plugin symlinks, so local
  Cursor UI testing needs a real directory copy under `~/.cursor/plugins/local/miller`.
- For a stable release, use `prerelease=false`; for a hyphenated version such as `0.2.1-beta.1`, use
  `prerelease=true`.
