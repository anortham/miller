# Miller Release Process

Use a two-step release path so publishing does not rebuild the full platform matrix.

## 1. Validate Packages

Run the release workflow with publication disabled:

```bash
gh workflow run release.yml \
  -f version=0.3.0 \
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
  -f version=0.3.0 \
  -f prerelease=false \
  -f publish=true \
  -f allow_overwrite=false \
  -f promote_run_id=<successful-package-run-id>
```

The promote path downloads the existing artifacts, verifies all archive checksums, and creates the GitHub
release without rebuilding the platform matrix.

For local promotion with the same checks:

```bash
scripts/release-promote.sh --version 0.3.0 --run-id <successful-package-run-id>
```

Use `--dry-run` to verify artifacts without creating or updating a release.

## Guardrails

- Do not publish from a failed, cancelled, or in-progress package run.
- Do not overwrite an existing stable release unless explicitly intended; pass `allow_overwrite=true` or
  `--allow-overwrite` only for that case.
- Keep `Directory.Build.props`, `miller-plugin.json`, `.claude-plugin/plugin.json`,
  `.codex-plugin/plugin.json`, `.claude-plugin/marketplace.json`, and release notes in sync before package
  validation.
- For a stable release, use `prerelease=false`; for a hyphenated version such as `0.2.1-beta.1`, use
  `prerelease=true`.
