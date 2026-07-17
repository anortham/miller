# Miller Release Process

Use a two-step release path so publishing does not rebuild the full platform matrix.

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

## Guardrails

- Do not publish from a failed, cancelled, or in-progress package run.
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
