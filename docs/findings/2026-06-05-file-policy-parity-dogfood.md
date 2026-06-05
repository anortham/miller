# File Policy Parity Dogfood

- **Date:** 2026-06-05
- **Miller checkout:** `/Users/murphy/source/miller`
- **Extractor tested:** initial `.tools/julie-extract`, `julie-extract 2.1.2`; follow-up restored
  `.tools/julie-extract`, `julie-extract 2.1.3`
- **Purpose:** Close TODO item 13 by checking whether Miller's delegated `julie-extract` scan path
  already matches Julie's richer walker/file-policy behavior.

## Result

Initial `julie-extract` 2.1.2 dogfood covered much of the policy Miller needs, but not all of
Julie's walker contract. Two gaps were real in the released 2.1.2 binary:

1. `vendor/` is indexed by `julie-extract` even though Julie full-index walking treats `vendor` as a
   blacklisted directory.
2. A nested workspace root does not inherit `.gitignore` rules from the git root above it. Julie has a
   regression test requiring this behavior for nested workspaces such as `/repo/packages/app`.

This means the file-policy parity follow-up was not merely a documentation proof. The patched
`julie-extractors` release is now available as v2.1.3, and Miller's local pin/restore path has been
updated to that binary. The exact Windows beta-candidate CI rerun remains tracked in the beta checklist.

## Live Fixture

Fixture shape:

```text
repo/
  .git/
  .gitignore                  # private_data/
  packages/app/               # scan root
    .gitignore                # ignored.rs, ignored_dir/, *.log
    .julieignore              # julie_ignored.rs, julie_dir/
    external.ignore           # extra_ignored.rs, extra_dir/
    src/keep.rs
    sub/.gitignore            # local_only/
    private_data/secret.rs
    vendor/pkg/index.rs
    node_modules/pkg/index.rs
    target/debug/build.rs
    dist/out.rs
    build/out.rs
    .cache/out.rs
    src/schema.generated.ts
    src/app.min.js
    src/huge.js               # > 1 MiB
```

Command shape:

```bash
.tools/julie-extract scan \
  --root "$workspace" \
  --db "$db" \
  --ignore-file "$workspace/external.ignore" \
  --json
```

Indexed files:

```text
private_data/secret.rs
src/keep.rs
sub/keep.rs
vendor/pkg/index.rs
```

Probe table:

```text
IN  src/keep.rs
OUT ignored.rs
OUT ignored_dir/a.rs
OUT julie_ignored.rs
OUT julie_dir/a.rs
OUT extra_ignored.rs
OUT extra_dir/a.rs
IN  sub/keep.rs
OUT sub/local_only/a.rs
IN  private_data/secret.rs
OUT node_modules/pkg/index.rs
IN  vendor/pkg/index.rs
OUT target/debug/build.rs
OUT dist/out.rs
OUT build/out.rs
OUT .cache/out.rs
OUT src/schema.generated.ts
OUT src/app.min.js
OUT src/huge.js
```

## What Already Works

`julie-extract` 2.1.2 correctly excluded:

- root `.gitignore` rules under the scan root;
- nested `.gitignore` rules below the scan root;
- `.julieignore`;
- explicit `--ignore-file`;
- `node_modules`, `target`, `dist`, `build`, and `.cache`;
- generated/minified suffixes;
- oversized source files.

## Gaps

### `vendor/`

Julie treats `vendor` as a blacklisted directory in `crates/julie-core/src/shared.rs`. The extractor
currently has `node_modules`, `target`, `dist`, `build`, and `.cache` in its hard-exclude directory set,
but not `vendor`, so `vendor/pkg/index.rs` was indexed.

### Git-Root `.gitignore` For Nested Workspaces

Julie has a regression test in `src/tests/utils/walk.rs` requiring a nested workspace to inherit `.gitignore`
rules from ancestor directories up to the git root. The extractor scan root was `repo/packages/app`, while
the git-root `.gitignore` at `repo/.gitignore` ignored `private_data/`. The extractor still indexed
`private_data/secret.rs`.

## Beta Impact

This is the highest-value remaining beta hardening item. It affects real agent use because indexing
vendored code or gitignored private/generated directories can pollute search results and waste index time.

Local upstream fix status:

- Added `vendor` to `julie-extractors` hard exclusions.
- Added git-root `.gitignore` inheritance for nested scan roots.
- Added focused discovery tests for both cases.
- Verified the patched CLI with the same live fixture; only `src/keep.rs` and `sub/keep.rs` were indexed.
- Verified upstream with `cargo xtask test changed crates/julie-extract-cli/src/discovery.rs`,
  `cargo xtask test default`, and `cargo fmt --check`.

## 2.1.3 Follow-Up Verification

- Release: `julie-extractors` `v2.1.3`, published 2026-06-05, target commit
  `c6121943da2eaf8abfa4475c3f3320ff52fa22b2`, not draft and not prerelease.
- Miller pin: `scripts/julie-pins.json` and `MillerExtractContract.PinnedJulieExtractVersion`
  now target `2.1.3`.
- Restore: `bash scripts/restore-julie-extract.sh` downloaded the macOS arm64 asset, verified sha256
  `c4a90671a66bcc5b002793b6d0acc2925c85152b9833e7dedcea6c47ab70c51d`, and installed
  `julie-extract 2.1.3`.
- Fixture: the same nested-workspace policy shape indexed only `src/keep.rs` and `sub/keep.rs`;
  `vendor/`, `private_data/`, `node_modules`, `.julieignore`, and `--ignore-file` paths stayed out.
- Local Miller gate: `scripts/test.sh all` passed 1,568 fast tests and 25 scale tests, and
  `dotnet build Miller.slnx -c Release` passed with 0 warnings and 0 errors.

Recommended next step: commit this pin-bump/docs slice, push it, and rerun the Windows restore/build/test
gate on the exact beta-candidate commit.
