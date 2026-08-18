# julie-extract 2.33.7 pin adoption

- **Pin moved:** `2.33.6` → `2.33.7`.
- **Upstream:** [`anortham/julie-extractors` v2.33.7](https://github.com/anortham/julie-extractors/releases/tag/v2.33.7).
- **Tag provenance:** `v2.33.7` resolves to commit `6a7ccf518f890beeb76db8b152e4d86c9c78b58d`.
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-18T01:36:10Z`.

## What changed

Julie 2.33.7 is a compatible store-scope patch. Public CLI, report, schema, and
versioned-store contracts stay the same. The planner now drops a journal name
when `identifier.name` appears in more than 16 files in the current view, so a
small C# save stays scoped instead of crossing over to a full-view resolve.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.33.7-aarch64-apple-darwin.tar.gz` | `ed217060cd2a4878e7f3019d0970613a1b784900b2ed68758b5b0c170e3a4ebe` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.7-x86_64-apple-darwin.tar.gz` | `288e52eb0da1ba3e1ff2ded16492c6fb7fb136bfafba27534cb86494b3194464` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.7-x86_64-pc-windows-msvc.zip` | `e572d74e25ede013b96907a7fac20f3d27c19c06b226bf80a6bd9eb079b3791a` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.7-x86_64-unknown-linux-gnu.tar.gz` | `9579cba2070e4a1914a5cad2601e0d0a340a6dc7c71f05514ca5ee19e231cf97` |

## Verification

- GitHub release facts, tag provenance, asset names, and four supplied SHA-256 values were checked before pinning.
- The restored Linux binary reports `julie-extract 2.33.7`.
- No Miller public schema, report, CLI, or MCP surface version moves.
