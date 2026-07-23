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
- this Miller checkout — explicit source/content/imported-text mode coverage.

Use `--cases <path>` to put the case file somewhere else, or `--force` to overwrite it.

The starter suite is Miller-native. Do not copy old Julie matrix rows verbatim without revalidating them against
current source; `WorkspacePool` is a historical regression case now covered by unit tests, not a live benchmark
case. Path-oriented file cases should use path-shaped queries such as `media/server` plus a scope like
`filePattern=src/media/**`.

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
- Imported external/web cases require the matching content corpus rows to exist before scoring them. When those
  rows have not been seeded, run a narrower slice such as `--tag file`, `--tag source`, or the maintained local
  case file.
- `mode=file --json` currently scores against normal symbol rows from matching files. There is no separate
  versioned file-result JSON object for this release; compact output provides the file-first human rendering.

## Takeover Agent Evaluation

The cross-tool query runner above is not the Miller/Julie takeover decision harness. The takeover controller
under `scripts/bench-agent-efficiency.py` runs paired agent tasks under the frozen
[`takeover-evaluation-v1`](contracts/takeover-evaluation-v1.md) contract and keeps task rows, prompts, ordered
evidence, and scorer inputs under an operator-owned external root.

For takeover v1, the controller exports graded anchor IDs with the private task rows and ordered anchor matches
with each private run row. Its generated command runs `decision-score`, which combines:

- recall@6, nDCG@6, MRR, and top-1 over ordered evidence;
- stabilized correctness and wrong-action rates;
- tool-output-token, tool-call, and p75 wall-time efficiency.

The controller enforces the external private root before a decision run. The C# scorer remains pure and emits
`decision_verdict=not_decisional`. `finalize-safe` recursively validates the closed aggregate schema,
recomputes every gate, verifies full selection identity, capability coverage, zero unresolved voids, and
retained-artifact hashes, then derives the only safe `pass|fail` decision. It never trusts a verdict supplied
by the private aggregate and never exports task IDs, prompts, evidence rows, actions, paths, symbols, or
private filenames.

`agent-score` remains the action-only and legacy-calibration scorer. It cannot produce a retirement decision.
Synthetic, non-sealed examples of the `decision-score` inputs live under `eval/takeover/fixtures/`.
