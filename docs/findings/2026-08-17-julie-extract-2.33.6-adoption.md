# julie-extract 2.33.6 pin adoption

- **Pin moved:** `2.33.5` → `2.33.6`.
- **Upstream:** [`anortham/julie-extractors` v2.33.6](https://github.com/anortham/julie-extractors/releases/tag/v2.33.6).
- **Tag provenance:** `v2.33.6` resolves to commit `8da9ef6ed4abcb4b0b6584db1b161a28b46ed4fe`.
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-17T19:43:01Z`.

## What changed

Julie 2.33.6 is a compatible store-resolve performance patch. Public CLI, report, schema, and
versioned-store contracts stay the same. Resolve now:

- loads one source file's symbols into a bounded in-memory index (at most 2048 symbols and
  4096 type facts per file, 256 files at a time);
- keeps whole-pass answers for repo-wide unique-type name lookups.

On a Miller family-store copy this removed 141,236 name-walk queries. Leftover follow-ups stay
in julie-extractors `TODO.md` §18.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.33.6-aarch64-apple-darwin.tar.gz` | `f2776127b390019267a62bc80bc8ebf3b6f8f05f734a45ecf7679a496eeb3093` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.6-x86_64-apple-darwin.tar.gz` | `2ceaa538bf12863956ca04ddc55effed266d2534b32cad887e95777592fc232b` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.6-x86_64-pc-windows-msvc.zip` | `eb6902328167e1c893c28031a1e4e6ce3d05b9815aa3185ea2723c48afcddf07` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.6-x86_64-unknown-linux-gnu.tar.gz` | `dfe9b9c39d3c93000afda052a7b9c300c16c398139a8f417ae5e587c6bf18ae5` |

## Verification

- GitHub release facts, tag provenance, asset names, and four supplied SHA-256 values were checked before pinning.
- The restored Windows binary reports `julie-extract 2.33.6`.
- No Miller public schema, report, CLI, or MCP surface version moves.
