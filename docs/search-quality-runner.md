# Search Quality Runner

Miller has a local cross-tool search-quality runner for comparing Miller, Julie, and Eros on hand-labeled
queries. The runner is checked in; generated cases and run artifacts live under `.miller/eval/search-quality/`
and stay out of git.

## Initialize Local Cases

```bash
dotnet run -c Release --project tools/Miller.SearchQuality -- init
```

This writes `.miller/eval/search-quality/cases.json` with starter cases for:

- `~/source/hermes-agent` — Python-heavy agent project.
- `~/source/openclaw` — large TypeScript/Swift/Kotlin-style app/tooling workspace.
- `~/source/MyraNext` — C#/Vue/SQL app.

Use `--cases <path>` to put the case file somewhere else, or `--force` to overwrite it.

## Run A Slice

```bash
dotnet run -c Release --project tools/Miller.SearchQuality -- run \
  --providers miller,julie \
  --repo MyraNext \
  --limit 5
```

Useful filters:

- `--repo NAME` limits to one repository from the case file.
- `--case ID` limits to one case.
- `--tag TAG` limits to cases tagged with a language or query type.
- `--providers miller,julie,eros:lancedb-hybrid-coderank` chooses provider adapters.

Runs print one CSV-like row per provider/case and a provider summary with `top1`, `top3`, `top5`, `misses`,
and mean reciprocal rank. A timestamped JSON artifact is written to
`.miller/eval/search-quality/runs/<timestamp>.json`.

## Provider Commands

Defaults are local-development friendly:

- Miller: `dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj --`
- Julie: `/Users/murphy/source/julie/target/release/julie-server`, falling back to `julie-server`
- Eros: `/Users/murphy/source/eros/.venv/bin/eros`, falling back to `eros`

Override them with `--miller-command`, `--julie-command`, `--eros-command`, or the matching environment
variables:

- `MILLER_SEARCH_QUALITY_MILLER_COMMAND`
- `MILLER_SEARCH_QUALITY_JULIE_COMMAND`
- `MILLER_SEARCH_QUALITY_EROS_COMMAND`

Eros requires the hub to be running and the case repository to map to a registered Eros workspace. Add
`erosWorkspaceId` to a repository entry in the local case file when the Eros id is not the same as the repo
name.

## Notes

- The runner is a comparison tool, not a product dependency.
- Generated reports should stay in `.miller/`.
- Miller indexes must be current before trusting scores. If a repo has an incompatible or stale Miller DB,
  rebuild it before running the suite.
