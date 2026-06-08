# Site token-savings measurement - 2026-06-08

## Scope

Measured a conservative public-site metric for Miller's GitHub Pages page using cloned open-source repositories
under `/Users/murphy/source`.

The workflow is file orientation: an agent wants the structure of a known file before deciding what to read or
edit. The baseline is reading the whole source file. The Miller path is compact `inspect <file> --limit 20`,
which returns a bounded symbol summary and keeps import/module noise collapsed.

This does not claim Miller replaces full source reads when the body is actually needed. It measures context avoided
before that point.

## Command

```bash
node tools/site-token-savings/measure-site-token-savings.mjs
```

The script runs the Release Miller binary at `src/Miller.Server/bin/Release/net10.0/miller`. Set
`MILLER_BINARY=/path/to/miller` to measure another build.

The sampled workspaces were first registered/refreshed with:

```bash
src/Miller.Server/bin/Release/net10.0/miller workspace full --path /Users/murphy/source/flask --json
src/Miller.Server/bin/Release/net10.0/miller workspace full --path /Users/murphy/source/express --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/zod --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/Newtonsoft.Json --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/gson --full --json
src/Miller.Server/bin/Release/net10.0/miller workspace open --path /Users/murphy/source/jq --full --json
```

## Results

Estimated tokens use the same conservative public shorthand as the site: UTF-8 bytes divided by four, rounded up.

| repo | language | file | source bytes | Miller bytes | est. tokens saved | savings |
|---|---|---|---:|---:|---:|---:|
| Flask | Python | `src/flask/app.py` | 65,423 | 1,678 | 15,936 | 97.4% |
| Express | JavaScript | `lib/application.js` | 13,977 | 1,321 | 3,164 | 90.5% |
| Zod | TypeScript | `packages/zod/src/v4/classic/schemas.ts` | 88,629 | 1,587 | 21,761 | 98.2% |
| Newtonsoft.Json | C# | `Src/Newtonsoft.Json/JsonConvert.cs` | 55,576 | 1,598 | 13,494 | 97.1% |
| Gson | Java | `gson/src/main/java/com/google/gson/Gson.java` | 58,251 | 1,637 | 14,153 | 97.2% |
| jq | C | `src/jv_parse.c` | 27,379 | 1,170 | 6,552 | 95.7% |
| **total** |  |  | **309,235** | **8,991** | **75,060** | **97.1%** |

## Interpretation

- Miller avoided about **300 KB** of source text across six file-orientation workflows.
- The compact outputs totaled **8,991 bytes** versus **309,235 bytes** of source files.
- The public-site headline uses **97.1% less context** and **about 75K estimated tokens avoided**.

This is intentionally conservative: it compares Miller only to the exact source file it summarized, not to broader
manual exploration such as `rg` plus multiple follow-up reads.
