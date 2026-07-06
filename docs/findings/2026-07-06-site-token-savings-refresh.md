# Site token-savings measurement refresh - 2026-07-06

## Scope

This refresh re-ran the public GitHub Pages token-savings metric on the current Miller build.

The measured workflow is still file orientation: an agent wants the structure of a known file before deciding
whether it needs the full source body. The baseline is reading the whole source file. The Miller path is compact
`inspect <file> --limit 20`, which returns a bounded symbol summary and keeps import/module noise collapsed.

This does not claim Miller replaces full source reads when the body is needed. It measures context avoided before
that point.

## Command

```bash
node tools/site-token-savings/measure-site-token-savings.mjs
```

The script runs the Release Miller binary at `src/Miller.Server/bin/Release/net10.0/miller` by default. Set
`MILLER_BINARY=/path/to/miller` to measure another build.

The script expects the sampled repositories under the directory that contains the Miller checkout. In linked
worktrees it resolves that source root through Git's common directory. For any other layout, set:

```bash
MILLER_SITE_METRICS_SOURCE_ROOT=/Users/murphy/source node tools/site-token-savings/measure-site-token-savings.mjs
```

Measured binary:

```text
1.4.3+e89236b508d1
```

The sampled workspaces were already registered in the local workspace registry from the earlier public-site
measurement. To reproduce from scratch, register and fully scan the same sample repositories first:

```bash
src/Miller.Server/bin/Release/net10.0/miller workspace full --path /Users/murphy/source/flask --json
src/Miller.Server/bin/Release/net10.0/miller workspace full --path /Users/murphy/source/express --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/zod --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/Newtonsoft.Json --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/gson --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/jq --full --json
```

## Results

Measured at `2026-07-06T00:34:33.785Z`.

Estimated tokens use the same conservative public shorthand as the site: UTF-8 bytes divided by four, rounded up.

| repo | language | file | source bytes | Miller bytes | est. tokens saved | savings |
|---|---|---|---:|---:|---:|---:|
| Flask | Python | `src/flask/app.py` | 65,423 | 1,674 | 15,937 | 97.4% |
| Express | JavaScript | `lib/application.js` | 13,977 | 1,321 | 3,164 | 90.5% |
| Zod | TypeScript | `packages/zod/src/v4/classic/schemas.ts` | 88,629 | 1,587 | 21,761 | 98.2% |
| Newtonsoft.Json | C# | `Src/Newtonsoft.Json/JsonConvert.cs` | 55,576 | 1,598 | 13,494 | 97.1% |
| Gson | Java | `gson/src/main/java/com/google/gson/Gson.java` | 58,251 | 1,637 | 14,153 | 97.2% |
| jq | C | `src/jv_parse.c` | 27,379 | 1,166 | 6,553 | 95.7% |
| **total** |  |  | **309,235** | **8,983** | **75,062** | **97.1%** |

## Interpretation

- Miller avoided about **300 KB** of source text across six file-orientation workflows.
- The compact outputs totaled **8,983 bytes** versus **309,235 bytes** of source files.
- The public-site headline remains **97.1% less context** and **about 75K estimated tokens avoided**.

The refreshed run differs from the 2026-06-08 note only in small compact-output byte counts. The public headline
does not need to change.
