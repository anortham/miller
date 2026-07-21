# Task 2 report — Corpus freeze, exclusions, findings skeleton

**Status:** COMPLETE. All 4 acceptance criteria met; `validate` exits 0; syntax check passes.

## Frozen SHAs

| Repo | Frozen SHA (local `main` HEAD at freeze) | Frozen worktree |
|---|---|---|
| miller | `59c2c79e8633940de5d394f73235f10acbe2c2b8` | `<scratch>/frozen-miller` |
| julie | `9d1d22c5dcca8509e412db96b6dbb5ff19d4311a` | `<scratch>/frozen-julie` |

`<scratch>` = `/private/tmp/claude-501/-Users-murphy-source-miller/df49671d-ef55-48b5-b537-7efdb9e2bce8/scratchpad`
Both worktrees created with `git worktree add --detach` from their repos. julie's local main was
`9d1d22c5` (brief said "current local main HEAD, record SHA" — recorded live, not the brief's guess).

## Index artifact ids

Indexed with worktree-built `miller` (`src/Miller.Server/bin/Release/net10.0/miller`, v1.13.0),
`workspace open --path <root> --full`, no semantic env. julie-extract binary version 2.16.0 on both.

| Repo | Workspace id (SHA-256 of root) | Artifact id | Symbols | revision |
|---|---|---|---|---|
| miller | `6772d4640d5de25305f25317098cc2cf62539ea3bc588bc5969bf375532fe894` | `artifact-1784654234183324000` | 49,276 | 1 |
| julie | `b3282901372258f13a2038b121f7f708a208797f350e5f5d0a89cd86888257bc` | `artifact-1784654260643605000` | 34,429 | 1 |

Both frozen roots are now registered in `~/.miller/workspaces.db` (throwaway bench registrations —
**lead should prune** these two workspace ids after the program completes). Each frozen root has
`.miller/{symbols.db, content.db, search.db, history.db}`.

## Exclusion hit list + row-count proof

`BENCHMARK_DOC_EXCLUSIONS` added to `build_corpus.py`, applied unconditionally to the **miller** corpus
only (repo-scoped in `excluded(path, repo)` — julie's graded answer docs sit at the same relative
paths in a different repo). Frozen at miller SHA `59c2c79`: **56 files** = the 5 docs the plan names ∪
53 grep-derived (every `docs/` file whose text contains a graded doc_id). The named
`2026-07-07-dead-code-candidates-dogfood.md` and `2026-07-19-model-benchmark.md` fall inside the 53;
the 3 named-only additions (`2026-07-21-encoder-comparison-fusion-v2-design.md`,
`2026-07-19-miller-semantic-integration-design.md`, `2026-07-21-fused-arm-encoder-benchmark.md`) either
carry no graded id or don't exist at the frozen SHA — excluded unconditionally anyway.

**Safety check:** none of the 56 is itself a graded answer doc. The two graded miller docs
(`docs/adr/ADR-0001-guidance-delivery-channels.md`, `docs/release-process.md`) are NOT in the list, so
no ground truth is removed from the corpus.

**Row-count proof (miller corpus):**

| | units | symbol cards | doc chunks |
|---|---|---|---|
| without benchmark exclusions | 19,465 | 13,905 | 5,560 |
| with benchmark exclusions | 17,032 | 13,905 | 3,127 |
| excluded | **2,433** | 0 | 2,433 |

All 2,433 excluded are doc chunks (0 cards; excluded files are md/csv/json). Full corpus
(miller+julie, exclusions on): 35,392 units, golden-set leak check PASS. Full 53-file grep hit list is
in the findings doc's collapsible section.

## Validate output (primary gate)

```
$ dotnet run --project eval/retrieval-eval -- validate \
    --queries eval/retrieval-eval/sets/dev/queries.jsonl \
    --corpus miller=<scratch>/frozen-miller --corpus julie=<scratch>/frozen-julie
corpus: 38 distinct doc references checked, 0 missing
queries: 82
OK: schema valid and composition minimums met
VALIDATE EXIT=0
```

Also `python3 -c "import ast; ast.parse(open('eval/model-bench/build_corpus.py').read())"` → AST OK.

## Files changed (ownership honored)

- Modified: `eval/model-bench/build_corpus.py` (added `BENCHMARK_DOC_EXCLUSIONS`, repo param on
  `excluded()`, manifest reporting block).
- Created: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` (skeleton: header, pre-registered
  T5 gates verbatim from plan §Global Constraints, R1 within-run-only note, frozen SHAs + artifact ids,
  validate proof, exclusion proof, empty Task 4/5/6 stubs).
- Outside repo: frozen worktrees + `.miller/` under scratch.
- NOT staged/committed (parallel-lead-commit mode). Removed `eval/model-bench/__pycache__` byproduct.
- Overwrote a stale unrelated `task-2-report.md` (a telemetry-canary report left under the same
  filename from a prior numbering) with this report.

## Miller MCP / API-shape evidence

MCP calls were not used — direct reads were faster and the orientation facts were verifiable by
reading the exact files (the MCP index also covers the main checkout, risking symbol ambiguity per the
plan caveat). API-shape evidence cited from source:

- `GOLDEN_SET_EXCLUSIONS` mechanism: `build_corpus.py:44-58` — tuple of repo-relative prefixes checked
  via `str.startswith` in `excluded()`. Mirrored `BENCHMARK_DOC_EXCLUSIONS` on the same mechanism.
- Workspace open/scan verb: `miller help` → `workspace open [--path DIR] [--full]  Register + index a
  directory (creates .miller/symbols.db)`.
- Validate contract: `eval/retrieval-eval/README.md:26-36` — `validate --queries --corpus <repo>=<dir>`,
  exit 0 ok / 2 validation failed; "resolves it on disk at the pinned commit".

## Judgment calls

1. **Static hardcoded exclusion list, not a build-time grep.** Matches the `GOLDEN_SET_EXCLUSIONS`
   pattern the brief points to, and honors R1's "frozen/pre-registered before numbers" principle.
   Derivation (grep at SHA 59c2c79) is documented in a code comment so it's regenerable.
2. **Repo-scoped to miller.** Brief says "applied unconditionally to the miller corpus". Threaded
   `repo` into `excluded()` so julie's identically-named `docs/plans/*.md` graded answers are never
   touched.
3. **Verified no graded answer doc is excluded** before finalizing — the exclusion is a cheat-sheet
   filter, not a ground-truth filter.
4. **julie main SHA recorded live** (`9d1d22c5`) rather than trusting the brief's unstated value.

## Concerns

- Two throwaway workspace registrations left in `~/.miller/workspaces.db` (frozen-miller,
  frozen-julie) — Task 6 reuses frozen-miller's `.miller/`; lead prunes both after the program.
- The 56-file exclusion is large (2,433 chunk units, ~44% of miller doc chunks). Expected — miller's
  `docs/plans` heavily cross-reference source paths — and is the correct anti-leak behavior, but
  Task 5 should note the miller doc-chunk corpus is materially thinner than julie's as a result.
