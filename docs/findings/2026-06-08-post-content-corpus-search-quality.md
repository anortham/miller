# Post-content-corpus search quality retest - 2026-06-08

## Summary

The post-content-corpus retest does not show a need to widen default symbol ranking beyond `name + signature`.
The current routing model held up:

- default symbol/file search handled the starter search-quality runner cases;
- `mode=source` handled source-body and SQL/table text;
- `mode=content` handled docs/config prose in the current Miller workspace;
- `mode=all-text` returned a useful mixed docs/source result set for explicit broad text searches.

No external or web imports were present in the local Miller content corpus during this run, so `mode=external` and
`mode=web` were not scored here.

## Commands

```bash
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --limit 10
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller,julie --limit 10
dotnet run -c Release --project src/Miller.Server -- refresh --workspace-id MyraNext --json --wait
dotnet run -c Release --project src/Miller.Server -- refresh --workspace-id openclaw --json --wait
dotnet run -c Release --project src/Miller.Server -- refresh --workspace-id hermes-agent --json --wait
dotnet run -c Release --project src/Miller.Server -- search "ReportMenuItems" --mode source --workspace-id MyraNext --limit 5 --json
dotnet run -c Release --project src/Miller.Server -- search "media/server" --mode source --workspace-id openclaw --limit 5 --json
dotnet run -c Release --project src/Miller.Server -- search "media/server" --mode file --workspace-id openclaw --limit 5 --json
dotnet run -c Release --project src/Miller.Server -- search "content export" --mode all-text --limit 5 --json
```

## Results

Search-quality runner:

| provider | total | top1 | top3 | top5 | misses | mrr |
|---|---:|---:|---:|---:|---:|---:|
| miller | 7 | 7 | 7 | 7 | 0 | 1.0000 |
| julie | 7 | 5 | 7 | 7 | 0 | 0.8571 |

Artifacts:

- Miller-only run: `.miller/eval/search-quality/runs/20260608T022528Z.json`
- Miller-vs-Julie run: `.miller/eval/search-quality/runs/20260608T022555Z.json`

Workspace refresh status for runner repos:

- MyraNext: `search.db` current, `content.db` current, 608 sources, 952 chunks.
- OpenClaw: `search.db` current, `content.db` current, 13,306 sources, 24,430 chunks.
- Hermes Agent: `search.db` current, `content.db` current, 2,588 sources, 8,620 chunks.

Mode checks:

- `ReportMenuItems` with `mode=source` in MyraNext returned
  `MyraNext/MyraNext.SqlDB/dbo/Tables/ReportMenuItems.sql` at rank 1.
- `media/server` with `mode=file` in OpenClaw returned `src/media/server.ts` at rank 1.
- `media/server` with `mode=source` in OpenClaw returned source-body import/use hits, with
  `src/media/server.ts` at rank 5. This is acceptable for body-text mode; path-oriented queries should use
  `mode=file`.
- `content export` with `mode=all-text` in Miller returned the active CLI/Eros contract and relevant source files.
- `content corpus sidecar` and `browser39` with `mode=content` in Miller returned the expected docs/skills guidance.

## Release Follow-Up Closure

2026-06-08 item-2 cleanup:

- Refreshed the starter OpenClaw file-mode cases to use path-shaped queries and scope fields:
  `media/server`, `mode=file`, `filePattern=src/media/**`, `language=typescript`.
- Guarded the starter suite against reintroducing the stale copied Julie `WorkspacePool` case. That identifier is
  historical regression coverage now, pinned by exact-name definition-vs-import unit tests rather than live
  benchmark rows.
- Kept `mode=file --json` on the existing search JSON contract: symbol rows from matching files. No versioned
  file-result JSON object is needed for this release because compact output already gives file-first human
  rendering, and path/line/snippet contracts live in `mode=content|source|all-text`.

Verification:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter 'FullyQualifiedName~SearchQualityCliTests.Init_WritesStarterSuiteToRequestedPath' --no-restore
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --limit 10 --tag file --timeout-seconds 120
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --limit 10 --timeout-seconds 120
```

Results:

| run | total | top1 | top3 | top5 | misses | mrr | artifact |
|---|---:|---:|---:|---:|---:|---:|---|
| file slice | 3 | 3 | 3 | 3 | 0 | 1.0000 | `.miller/eval/search-quality/runs/20260608T130009Z.json` |
| Miller-only maintained local cases | 7 | 7 | 7 | 7 | 0 | 1.0000 | `.miller/eval/search-quality/runs/20260608T130023Z.json` |

Release decision: keep default symbol search narrow and keep file-mode JSON compatible for the next release.
