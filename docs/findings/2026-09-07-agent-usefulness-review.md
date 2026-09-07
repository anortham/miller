# Agent usefulness review of Miller 1.28.1

Date: 2026-09-07. Measured source: `40f7c71a7703` (main, clean tree). Reviewer: an AI coding agent
(Claude Code) using Miller through the plugin MCP server on the maintainer's Linux machine, with
subagents for parallel review and adversarial verification.

Question asked: from the standpoint of an AI coding agent, is Miller useful, are its guidance channels
right, would the agent use continuous testing, and what makes the agent want to stop using it.

## Method

- Three workflow batches: five static reviewers (guidance channels, hooks, CT adoption, skills,
  renderers); nine live tool trials plus a Miller-only versus raw-tools A/B on five identical tasks;
  twelve adversarial verifiers, one per headline claim, plus three critics (a bug-fix session, a
  refactor session, and a stop-using case).
- A live continuous-testing trial in this workspace: daemon start, an explicit run, and a warm re-run.
- Thirty days of `~/.miller/telemetry.db`, the registry, the family store on disk, and today's logs.
- Every claim below survived a verifier that was told to refute it. Where a verifier corrected a
  number, the corrected number is used and the original is noted in "Corrected claims".
- No edits were applied to the repo. Every `edit` call ran with `apply=false`. The CT daemon was
  started and left running (it was stopped before the review).

Not measured: incremental CT runs after a real edit (no edits were applied to the main tree), and
Miller on Windows or macOS.

## Answers

### 1. Are the server instructions and tool descriptions as helpful as they can be?

No. The tool descriptions are good: each states what, when, when-not with the better tool named, and
one copyable example, and tests enforce that shape. The embedded core is not good enough for its one
job, discovery:

- The core is 1,871 chars against the 1,900 cap. About 35% of it (641 chars) is a negative list of
  binding mechanisms that do not apply to the reader, naming `GOLDFISH_WORKSPACE` (a variable Miller
  never reads) and `primary` four times. Seven of the ten tool routing lines are two to four words, for
  example `content — external text.` and `context — unfamiliar areas.` An agent that sees only the
  core cannot learn that content needs an import first or that impact with no args reads the git diff.
- The descriptions omit facts that cost calls: compact `impact` cuts at 6,000 chars with no
  continuation (the contract doc says so, the description does not); `search` clamps `limit` to 10
  silently; `regions=` replaces `mode` instead of restricting it; `mode=text` is undocumented and is
  a symbol-name search; `workspace remove` is not described although the routing block tells agents
  to call it.
- Jargon a fresh agent cannot decode is pinned by tests: "pinned index", "semantic-broker readiness,
  role, backend, accelerator lease", "usage_evidence=unavailable", "value_declaration_complete".
- CLI and MCP names drift: `tests serve` versus `operation=start`, `--arm` versus `retrieval`,
  `dashboard` as a verb versus a workspace operation.

### 2. Do the session-start hooks send the right message?

Mostly, with three real problems.

- Delivery works and is proven: the block reached this session and every subagent. One source of
  truth (`hooks/miller-routing-block.md`, `miller rules`, the setup snippet) stays byte-identical.
- The hook ignores its stdin, which carries `cwd`, and instead tells the agent to call
  `workspace list` every session. The display id is `sanitizeLeaf(basename(realpath(cwd)))` plus the
  first 12 hex of `sha256(realpath(cwd))`; the verifier re-derived all 93 registry rows from that rule
  on Linux. The simplest correct injection is the realpath of the cwd itself, since the selector
  accepts a registered root path on every platform. Caveats: lowercase before hashing on Windows and
  macOS, and anchor on `CLAUDE_PROJECT_DIR` for subagents because `cwd` moves after `cd`.
- Three overlapping Miller blocks land at session start: the hook block (3,298 chars), the MCP core
  (1,839), and the razorback plugin's toolchain table (2,295 in the main session, 2,498 for
  subagents). About 7.5 KB, or roughly 1,900 tokens, per subagent, and the razorback copy lacks both
  `tests` and `workspace_id`.
- No numbered rule names the `tests` tool. Rule 3 says "which tests to run" and routes to `impact`.
  The `tests` line is the tenth of ten. Rule 8 (prune at session end) is the longest rule and asks
  for a session-end action at session start.

### 3. Is the agent aware of continuous testing, and will it use it or ignore it?

Aware, yes. Use it, not on first contact. The live trial is in the CT section below. In short: after
a three-day gap, `tests start` returned at once, `tests run wait=true` gave up after 5 seconds with
`verdict=unknown unacked`, the daemon loop then spent 11 minutes 7 seconds at full CPU inside impact
selection with no log line and no command processing, `tests status` called it wedged and told the
agent to stop it, and following that hint would have killed legitimate work and replayed the same 11
minutes on restart. The bare fast suite ran in 65 seconds wall. A warm explicit run later took 52
seconds.

Adoption evidence: in 30 days, 227 edits were applied through Miller; 3 of them (1.3%) were followed
within ten minutes by a `tests run` call, 7 by any `tests` call, and 136 (60%) by an `impact` call.
The ledger records only explicit tool calls, so daemon auto-runs and shell test runs are invisible,
but the number says agents do not reach for the tool.

What would change that: a rule beside rule 3 that names the sequence (edit, then `impact`, then
`tests status`, then `run wait=true` when the stale count is small); a stall detector that stamps the
loop tick during selection; a status line that says what the daemon is doing and how long it will
take; and an `impact` result that hands off to `tests` when CT is enabled.

### 4. Are the other tools useful: accuracy, speed, token efficiency?

Accuracy is the strong point. Speed is acceptable for search and inspect and poor for context,
impact, and edit. Token efficiency is good per call and bad per task because of output shape.

| Tool | Would this agent use it | Accuracy observed | Main cost |
|---|---|---|---|
| search | often | symbol, file, markers, regions exact; `mode=source` and `mode=content` unreliable for phrases | OR-fallback tails, hidden test hits |
| inspect | often | 22/22 and 8/8 symbols exact; constants authoritative | 10-row file cap, repeated headers on continuation pages |
| context | sometimes | good when the query names a symbol; wrong for "why" questions, `edited_files`, absolute-path stack traces | 1,450 to 1,500 tokens per call regardless of question; 8 to 13 s average over 30 days |
| trace | often | scoped refs exact; overloads return 0 refs | rows doubled by fallback duplicates; bridge 3 to 4 s per call |
| impact | sometimes | hop-1 exact for a real symbol; homonym edges inflate results | tests section cut by the 6,000-char cap; 50% of bytes are `via=` hashes |
| edit | sometimes | match proof honest; body replace can preview malformed code | 2.3 to 2.7 s floor per call on this index |
| patterns | sometimes | parser-exact counts | compact drops the value column (URLs) |
| content | sometimes | line numbers exact | lines cut at 160 chars; `source_id` ignored on search |
| workspace | sometimes | every number exact | freshness word contradicts the read banner |

The A/B: five identical tasks, one agent per arm.

| Arm | Miller calls | Est. tokens read | Correct |
|---|---|---|---|
| Miller-only | 53 | 55,059 | 5 of 5 |
| Raw tools (rg, sed, Read) | 14 | 9,180 | 5 of 5 |

The verifier showed the 6x gap is not inherent. One task ("which tests if ExtractJobsPolicy changes")
cost 26 calls and 42,271 tokens because compact `impact` cut the likely-tests section and its hint
said "use format=json", so the agent paged the full depth-2 JSON twice. A single
`impact target=ExtractJobsPolicy limit=30 max_depth=1` answers the grep-proven part in 803 tokens.
Without that task the gap is 1.56x (12,704 versus 8,155 tokens) for answers that carried enclosing
symbols, line ranges, and pinning tests that rg cannot give. The recorded calibration in
[2026-08-25-miller-vs-bare-agent-v1.22.1-calibration.md](2026-08-25-miller-vs-bare-agent-v1.22.1-calibration.md)
points the same way: more tasks solved at several times the tool-output tokens.

### 5. Is there anything that makes the agent want to stop using it?

Yes, five things. None is about answer quality. All have a file:line cause.

1. Every workspace-bound read prints `freshness: refresh_pending` while `workspace status` on the
   same revision prints `fresh`, and the banner never clears on a reader even after a refresh
   completes. Rule 6 then sends the agent to `workspace refresh`, which on this reader is a 2-second
   `lock_busy` refusal. Nothing in context defines the word.
2. The CT first contact above: an 11-minute silent stall, a wrong "wedged" verdict, and a recovery
   hint that would discard the work.
3. Disk: `~/.miller` is 55 GB. This repo's family store is 19 GB for a live index of 224,700 symbols;
   `store.db` holds 1.81 million symbols and 3,545 manifests of retained history, 15 times the 1.0 GB
   legacy artifact. The julie-extractors family is 25 GB, 12 GB of it sidecars for 28 members, most
   of them dead worktrees. CT logs `generation_disk_over_budget bytes=49.7 GB budget=21.4 GB` on
   every run and no status surface shows it.
4. A 2.3 to 2.7 second floor on every `edit` call on this index, including disk-only text replaces
   whose own proof says `index not_used` and including refusals. Cause: `WorkspaceIndexProvider` is
   transient, so each edit reloads the full symbol projection. A 5,318-symbol workspace pays 74 to
   143 ms for the same path.
5. Output shape that forces extra calls: the `impact` cap, doubled `trace` rows, 10-row `inspect`
   listings, `via=` hashes, `site=` ids, store hashes in `workspace status`, and the prune nudge on
   most workspace calls.

### 6. Obvious gaps the agent would have used

Ranked by how often they would bite, from the two persona walkthroughs and the trials.

1. A runnable test selector. `impact` names likely tests but no command; `tests status` has a
   per-project `command` field that is always null. Every failing-test session and every CT-off
   session stops at "run those with your test runner".
2. A tests-only impact view with exact/heuristic split. Homonym edges (`TryWrite` on
   `ChannelWriter<T>`) put 12 noise tests among 31 and 7 noise rows among 10 impacted symbols.
3. A members view for a class: every member with signature, visibility, and line, no body. Today a
   116-member class shows five private fields at overview and needs seven paged calls to enumerate.
4. Batch or multi-hunk edits. A parameter addition needed 13 edit calls for 13 sites.
5. Homonym-aware rename. Exact mode refuses when a fallback candidate is a different type's member;
   `include_fallback` would rename the wrong calls and break the build.
6. Rename coverage of XML doc `cref`, comments, strings, markdown, and `<Old>_*` test names, at
   least as a listed "not renamed" group.
7. A `context` disposition that is a relevance check. Today `evidence=sufficient` means "some pivot
   has a body". `edited_files` seeds the file's own symbols, never its callers.
8. CT build failures with a log. A discovery failure keeps one line ("Unable to find the specified
   file"), the workspace verdict goes red for an unrelated eval project, and the hint points at a
   test file that does not exist. This happened live today when a bare `dotnet test` built the tree
   while the daemon rediscovered a project.
9. A freshness banner computed from evidence, with the word defined in the routing block.
10. `content` search scoped to one `source_id`, full lines on read, and literal-first ranking.

## Live session-start experience

Three overlapping Miller guidance blocks landed in context before the first user message:

| Channel | Source | Chars |
|---|---|---|
| SessionStart hook routing block | `hooks/miller-routing-block.md` | 3,298 |
| MCP `ServerInstructions` core | `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` | 1,839 |
| Razorback plugin toolchain table | `razorback/skills/using-razorback/SKILL.md` (not Miller's file) | 2,295 |

The first workspace-bound call still required `workspace list` to learn `miller-91250a0fd4f3`.

## CT live trial (this workspace, 2026-09-07, UTC)

State before: `enabled: true`, `daemon: stopped (daemon gone)`, `stale: 9738` of 9,738, last run
2026-09-04 at revision 80592, live revision 87701, a gap of 282 changed files.

| Time | Event |
|---|---|
| 14:02:40 | `tests start` (368 ms): "the daemon is status-only until a change" |
| 14:02:59 | `tests run wait=true wait_seconds=240` returns after 5 s: `verdict=unknown unacked`, no next step |
| 14:02:46 to 14:14:08 | no loop tick; 97 to 100% CPU, 0.8 to 1.8 GB RSS; the loop thread sits in `ContinuousTestImpactSelector.SelectAtRevision` -> `AddIdentifierReferenceEvidence` -> `QueryTimeResolutionReader.ReadInboundExact` |
| 14:04:36 onward | `tests status`: `daemon_loop: loop_stalled`, hint `tests operation=stop — the daemon loop is wedged; stop, then start` |
| 14:05:56, 14:09:03, 14:12:25 | one log line per project: `ct enqueue no-run ... outcome=Unknown` |
| 14:14:06 | the run request from 14:02:59 is acknowledged, 11 min 7 s later |
| 14:14:18, 14:14:28 | the two eval projects run and pass |
| 14:15:06 to 14:17:39 | `Miller.Tests` runs 9,632 of 9,980 cases in one chunk, 153 s, passed |
| 14:19:12 | status `verdict: green`, `stale: 0` |
| 14:29:54 | while a bare `dotnet test` built the tree, CT rediscovery of `FusionArm.Tests` failed (`FileNotFoundException`, one line kept); workspace verdict red |
| 14:30 | a warm explicit `tests run wait=true` completes in 52 s; verdict green again |

Baseline on the same machine under review load: `dotnet test --filter "Category!=Scale"` passed
10,026 tests in 39 s test time, 65 s wall including build.

What the verifier established from code and logs:

- Selection runs synchronously on the loop thread (`ContinuousTestDaemonHost.CollectCompletedPollReads`
  -> `ApplyPollResult` -> `ContinuousTestRevisionPoller.ApplyRead` -> `ContinuousTestDaemonQueue.Enqueue`
  -> `SelectAtRevision`). The tick is stamped only at the top of a pass and after a drain, and
  commands are processed only at the top of a pass, so a slow selection is indistinguishable from a
  wedge to `CtDaemonLoopHealth.Evaluate` and to `tests run`.
- Each of the three selections seeded all 85,995 symbols of the changed files, ran an inbound
  identifier read of about 510,000 rows through 10,427 per-name SQL queries plus a discarded forward
  pass, resolved them in memory, and then returned Unknown because the graph impact read (limit 100)
  was truncated, a fact known before the expensive read started. The result was discarded three times.
- Following the stall hint would have killed the daemon after 10 s, left the cursor at 80592 (same
  replay and same stall verdict on restart), and rejected the persisted Run request as `no-live-key`.
- `tests status` then hints `tests operation=failures — inspect recent results` on a workspace with
  zero red cases; `failures` hints back to `status`.

## Telemetry, last 30 days, all workspaces

| tool / op | calls | errors | empty | avg ms | p95 ms (tool) | avg est tokens |
|---|---|---|---|---|---|---|
| inspect full | 19,223 | 0 | 2,904 | 1,218 | 2,733 | 886 |
| search source | 12,335 | 196 | 4,048 | 535 | 1,516 | 476 |
| search content | 8,881 | 102 | 2,294 | 496 | | 400 |
| search symbol | 7,817 | 503 | 495 | 479 | | 470 |
| inspect overview | 6,446 | 1 | 1,197 | 1,110 | | 647 |
| trace refs | 1,494 | 1 | 378 | 1,180 | 5,967 | 715 |
| impact target | 1,371 | 1 | 342 | 2,054 | 13,808 | 860 |
| impact changed_paths | 436 | 0 | 139 | 12,003 | | 872 |
| impact git_diff | 336 | 1 | 75 | 16,714 | | 1,016 |
| context (default) | 691 | 0 | 19 | 12,713 | 44,192 | 1,339 |
| context usage | 431 | 0 | 10 | 48,901 | | 1,646 |
| edit replace_text | 738 | 91 | 146 | 3,749 | 8,195 | 172 |
| workspace refresh | 719 | 20 | 145 | 8,394 | 7,928 | 118 |
| workspace open | 148 | 3 | 5 | 27,563 | | 92 |
| patterns search | 296 | 0 | 27 | 259 | 1,020 | 1,042 |
| content read | 1,055 | 2 | 33 | 27 | 339 | 1,190 |
| tests status | 156 | 2 | 2 | 48 | 10,149 | 243 |
| tests run | 21 | 0 | 0 | 124,642 | | 137 |

Errors: 1,037 rows are `Search sidecar for view '...' is missing or stale. Run \`miller workspace
refresh\` to converge it.` (1,286 with the sibling Content-sidecar message). 1,012 of them landed on
2026-08-12 to 16 in this workspace on Miller 1.18.1 to 1.19.3 during sidecar development; 21 since
2026-08-22; 0 on 1.28.x. The throwing path is unchanged at HEAD, the text is CLI syntax, and
`ToolDiagnostic.FromException` classes it `internal_failure` with an empty `next_actions` list, so an
agent that hits it on a freshly opened workspace sees no MCP next step. The 91 edit errors `The
family-store generation changed before its lazy repository was loaded` cluster on 2026-08-20 to 27
and stop after the producer fix.

Context latency by week (ok calls): week 34 average 8.4 s with 138 of 425 over 10 s; week 35 average
13.3 s with 107 of 295 over 10 s. In this session warm context calls took 1.0 to 2.0 s; the cold
first call took 9.8 s.

## Disk

`~/.miller` = 55 GB. `stores/` = 54 GB: this repo's family 19 GB (`gen-001/store.db` 15 GB, page
count 3,777,137 with freelist 0, sidecars 3.8 GB), julie-extractors family 25 GB (`gen-001` 13 GB,
sidecars 12 GB, 53 sidecar databases for 28 members). Miller runs `store maintain gc --apply` and
reads only `pruned_request_rows`; retention policy is the producer's. The registry holds 93 rows, 39
with no root on disk; 33 of the 39 are removable by a real `workspace prune` today and the other 6
(scratch roots under `/tmp` with a store view) only by `workspace remove path=<root>`. The prune
output flags the whole call as a refusal whenever any row fails retirement, so 40 of the 52 flagged
prune calls still listed 30 to 33 prunable rows and telemetry logged them as empty.

## Verified findings by area

Severity: high means an agent gets a wrong answer or a wrong next step; medium means wasted calls,
tokens, or seconds on most sessions; low means cosmetic or rare.

### Freshness and workspace

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | Every default read prints `refresh_pending` on a fresh index from a reader whose primary bootstrap was deferred (the plugin server), and the word never clears even after a refresh completes; `queue: empty` in status is vacuous on a reader | `ReadToolWorkspaceRouting.cs:25` maps a named id to the Background arm; `WorkspaceIndexProvider.cs:1044-1048` sets `RefreshPending` unconditionally; `WorkspaceFreshnessView.cs:28-30` shows the word only for a healthy row; the background result is never stored | print `fresh (rev N, background check queued)` when served revision equals the registry revision; reserve `refresh_pending` for a started refresh; define the word in rule 6 |
| high | Rule 6 sends the agent to `workspace refresh`, which on this reader waits the full 2 s lock window and refuses `workspace_refresh_lock_busy`; a reader with a bound primary refuses `not_leader` in 1 ms | `WorkspaceTool.cs:1244` sees no bound primary; `CrossWorkspaceRefreshService.cs:29` `DefaultLockBusyWait = 2s` | return `delegated: leader pid N alive, rev matches` at once; reword rule 6 to "repeat the call with ensure_fresh=true" |
| medium | `workspace health` compact prints 1 of 3 warnings; the dropped one is `indexer_leader_version_mismatch`, the fact that explains the two rows above | `WorkspaceRender.cs` health compact cap | print up to 3 warnings and actions, leader and version mismatches first |
| medium | `workspace list limit=5` returns only the 5 error rows and omits the current repo; the description says recency-ordered | `TakePreferringErrors` pins error rows into the cap | state the pin rule in the header; cap pinned errors at a quarter of the limit |
| medium | `workspace status` carries about 470 chars of store provenance (family, view, 64-hex manifest, member list) | `WorkspaceRender.cs:535,714-723` | keep level, generation, and member count; move the rest to health |
| medium | `workspace onboarding` presents a two-week-old failure spike (488 symbol-search errors) as current friction | 30-day window mixed across builds | bucket friction by current `miller_version` or a 7-day window; name the error class, not the exception type |
| low | The prune nudge repeats on most status, onboarding, and list calls in a session | `WorkspaceTool.cs:689-711` recomputed per call | emit once per process, or only from onboarding |

### Impact

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | Compact renders impacted rows first and likely tests last, then cuts at 6,000 chars, so tests are what the cut removes (8 of 67, 0 of 58 visible); compact never reaches the paging envelope; the description omits the carve-out | `ImpactTool.cs:1356-1397`, `BoundCompact` 1611-1623 | render likely tests first or reserve a fixed slice; give compact a continuation; say so in the description |
| high | Name-only homonym edges enter impacted rows and likely tests with no tier in compact; hop-2 rows reached through a heuristic hop read as exact | identifier_name edges without receiver-type agreement | add `evidence=exact` or split `likely tests (exact)` from `(heuristic)`; drop identifier edges whose receiver token is not the parent type |
| high | git-diff mode omits the tests whose own bodies changed | changed test ranges are not seeded as hop-0 tests | seed changed test methods first |
| medium | `via=<32hex> edge=... source=...` is 50.1% of the compact payload; the seed id repeats on every hop-1 row and resolves to nothing in compact | `ImpactTool.cs:1439-1440` | drop `via` for hop 1; print a name for hop 2; one flag for exact versus heuristic |
| medium | JSON pages are raw 6,144-byte fragments that cut mid-object; a 55,118-byte result costs 9 calls, each re-running the traversal | JSON renderer emits no newlines; pager cuts bytes | page by rows; make `limit` bound JSON size |
| medium | `limit` is shared between impacted rows and tests, ordered by centrality, so tests are cut first | one budget | separate budgets; fill tests first |
| low | No `dotnet test --filter` line and no Scale marker on test rows | | one runner line per test file from the CT provider; a `[scale]` tag from the class trait |

### Trace

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | An overloaded method returns 0 references from trace, inspect, and impact (`FullRebuildPromotion.Promote`: 11 rg sites) | `QueryTimeResolver` resolves a tier only on exactly one candidate; `ReferenceEvidenceReader` suppresses name fallback when more than one definition shares the name | attribute qualified call sites to every overload in the class; never suppress fallback for an explicit id or scope |
| high | `mode=bridge` prints "frontend and backend route facts exist for /zzz/nope" for routes that do not exist; every bridge call takes 3 to 3.8 s | route lookup happens after the sentence; provider graph rebuilt per call | look the route up first; cache the provider graph per revision |
| medium | Refs hold 68 rows for 32 code lines: 30 exact, 6 spanless member-to-member rows, 32 name-fallback rows that repeat 30 of the same lines; the header counts page rows (17), and the 12 KiB page fits about 18 rows | `TraceTool.cs:1187/1231` dedupes by site id only | merge a fallback row into the exact row on the same line; report unique sites; drop `site=` ids from compact (about 64% of a row) |
| medium | Path mode cannot start from a class or from `Program.cs`, and `path_kind=call` misses method-group references and the last identifier hop to a class | | expand a class source to its members; treat a method group as a delegate call edge; say when a dependency path exists |
| medium | `reference_kind=type_usage` misses non-nullable method return types (5 of 42 lines) | extractor fact gap | emit a type_usage fact for return types in julie-extractors across all languages |
| medium | Bridge misses razor-interpolated htmx URLs and reports the component "has none" while 3 `hx-post` facts exist | extractor drops the interpolated URL | keep the static path prefix as the route fact |

### Search

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | `mode=source` for a phrase is an AND of tokens with stop words dropped, shows rows without the phrase, and silently hides rows: 3 test chunks plus one production file, `TestsCore.cs`, misflagged as a test | `ContentCorpusWriter.IsTestPath` (`ContentCorpusWriter.cs:1093-1104`) uses substring `test` and `spec` on the file name, diverging from `TestPathClassifier`; 114 sources are misflagged in the live corpus, including `InspectTool.cs` via "spec" | use `TestPathClassifier`; print a "N test rows hidden" line; rank phrase matches first with a `relaxed=or` note |
| high | `mode=content` order does not follow the JSON `score`: rows are ranked by reciprocal-rank fusion of lexical and semantic arms while `score` is lexical only; bag-of-words rows outrank phrase rows | `SearchTool.cs:3163-3282` | report the fused score, or rank phrase-bearing rows first |
| medium | OR fallback lets one short common term (`rescan`) drown the symbols that match the rarer terms | fallback ranks by BM25, not by distinct terms matched | rank by distinct query terms matched first |
| medium | The markers empty-result hint sends the agent to a `mode=source` search that can only find the marker vocabulary (643 tokens of noise) | `CrossToolHandoff` marker path | name markers that exist outside the requested set instead |
| low | Symbol mode appends a low_signal "Other matches" block that costs more tokens than the answer; `limit` above 10 is clamped with no notice; `mode=text` is undocumented | | fold low_signal rows into the count; print the clamp; document or remove `text` |

Strength kept on the record: the semantic arm rescued the natural-language backoff query
(`ScanFailurePolicy` at lexical rank 15, semantic rank 6, final rank 2, 332 tokens) where rg costs
about 4,900 tokens.

### Inspect

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| medium | File listings cap at 10 rows and `limit` above 10 is clamped silently; 41% of test files here have more than 10 test methods, so rule 2 costs 2 to 3 calls | `InspectTool.cs:57` | raise the listing page to 30 to 40 rows; print the clamp |
| medium | Continuation pages repeat the doc, children, refs, and callees (about 900 of 1,504 tokens on page 2) | | emit only the symbol header and the body slice on continuation |
| medium | Children are capped at 10 even at `depth=full` and the hint says "use depth=full" while already there; overview shows the first five children by line, so a 116-member class shows five private fields | | list every child at full or page them; rank public members first |
| medium | Ambiguity output lists 7 candidates but 3 `Try` lines, never shows the parent type, and falls back to 32-hex ids on the second round; `ambiguous_target` is the top inspect empty code (890 since 1.24) | | show `[parent=Type]` per candidate; suggest `Parent.Member`; render all candidates when they share one file |
| low | A not-found target is the slowest inspect call (844 to 884 ms versus 86 to 140 ms for a hit) | fuzzy and semantic ladder runs after exact misses | bound the miss path; return three near-miss names |
| low | Doc comments print with `///` and XML tags (about a quarter of the doc bytes); a constant at full prints a "body unavailable" section although `value_declaration_complete=true` | `InspectTool.cs:791-805` | strip markup; omit the body section for complete values |

### Context

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | `evidence=sufficient reason=pivot_implementation_present` is a body-presence check, so bundles that lack the deciding code are labeled sufficient | `ContextBundleBuilder.cs:1707-1715` | compute disposition from anchor coverage; emit `insufficient` when anchors did not match |
| high | `edited_files` seeds the edited file's own symbols only (4-pivot cap), never its callers; an unrelated Python test took a slot | `ContextBundleBuilder.cs:614-630,741` | seed pivots from references to the file's public symbols (usage mode already lists call sites) |
| high | A .NET stack trace with absolute `in /abs/path.cs:line N` frames produces no stack-frame pivots; relative paths resolve | `ResolveIndexedFilePath` and `FindByFilePathFragment` miss absolute paths | canonicalize absolute frame paths against the workspace root; report unmatched frames |
| medium | Natural-language queries pivot on giant wrapper classes and constants (`CliDispatch`, a 4 KB `HelpText`), and the snippet budget goes to their preambles | pivot ranking ignores kind and size | penalize constants, namespaces, enum members, and classes over about 200 lines; start snippets at the matching member |
| medium | Latency: 30-day average 8 to 13 s with a quarter of calls over 10 s, against a description that says "first call in an unfamiliar area"; warm calls in this session 1 to 2 s | | keep the projection warm across calls |

### Edit

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | `replace_symbol_body` accepts a `new_text` that begins with the target's own signature and previews a malformed member (signature twice, stray `;`) with no warning | verbatim `[body_start, body_end)` replace | refuse when `new_text` starts with the signature; state the body-only contract in the description |
| medium | Every edit call on this index pays 2.3 to 2.7 s, including disk-only `replace_text` (`index not_used`) and refusals; 74 to 143 ms on a 5,318-symbol workspace | `WorkspaceIndexProvider` is transient, so `WorkspaceEditContextFactory.Create` runs `SymbolSearchProjectionLoader.LoadSession` on every call | parse the operation first and load the projection lazily for symbol ops; cache the projection by snapshot key |
| medium | `rename_symbol` misses XML doc `<see cref>` sites (3 of 4, 6 of 7) and cannot exclude homonym fallback sites, so exact mode refuses and `include_fallback` would rename `ChannelWriter<T>.TryWrite` calls | | list doc, comment, string, and `<Old>_*` test mentions as "not renamed"; receiver-aware homonym rejection; `exclude_sites=` |
| medium | Exact rename refuses when the only uncovered sites are same-file direct calls recorded without byte spans, and the text says "refresh the workspace" | spanless relationship sites | locate the token inside the containing symbol's span and promote it |
| low | `insert_after` consumes one leading newline; `replace_symbol_signature` glues the brace onto the signature line on Allman-style code; `no_match` names no nearest candidate | | preserve `new_text` verbatim; keep original whitespace before `body_start`; append the closest line |

### Patterns and content

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | Patterns compact rows drop the fact's value (URL, href, route target) and keep constant keys (`framework`, `query_family`, `pattern_version`) on every row | `MetadataPriority` | value keys first; hoist constants to the header |
| medium | `aspnet.minimal_api.route.v1` misses `MapMethods` (22 of 30 dashboard routes); `htmx.attribute.v1` misses `hx-*` set through dictionary literals in razor `@code` (13 of 51) | extractor recognizers | add both in julie-extractors; say what is covered until then |
| high | `content read` cuts every line at 160 chars in compact and JSON with no parameter to widen it | `MaxReadLineUnits = 160` | add `max_line_chars`; full lines when `context_lines=0` |
| high | `content search` ignores `source_id`, so a fresh import is crowded out by a 13.7 MB older log; a hyphenated query returns loose token matches with no label | | honor `source_id`; literal-first ranking with a `relaxed=or` note |
| medium | `content search workspace_id=all` took 36.8 s over 93 registry rows, 39 with missing roots | probes dead roots | skip missing and error rows before opening sidecars; print `searched=N degraded=M` |

### Tests (CT)

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | Selection runs on the loop thread, so a long selection reads as a wedge; the stall hint (`stop`) would discard the work and the persisted run request | `ContinuousTestDaemonHost.cs:606,702,1252,1328`; `CtDaemonLoopHealth.Evaluate` | stamp the tick inside selection phases; publish `activity: selecting <project> (N of M)`; suppress the wedge hint while a phase reports progress |
| high | Each selection after a revision gap seeds every symbol of the changed files and runs a 510,000-row identifier read that is discarded because the graph read was already truncated | order of checks in `SelectAtRevision` | check the graph truncation before the identifier read; bound the seed set; or mark stale from `files.content_hash` deltas without resolution |
| high | `tests run wait=true` gives up at the 5 s ack timeout and reports `unacked` with no next step; the ack landed 11 minutes later | join path only joins an Executing or Queued daemon | report "queued behind selection, poll status"; extend the wait to the caller's `wait_seconds` |
| high | A CT discovery failure keeps one line of build output, turns the workspace verdict red for an unrelated eval project, and `failures` hints at a test file that does not exist | no build-log artifact | persist build output as a run artifact; render a discovery failure as its own row |
| medium | The status hint ladder has no branch for stale cases: stale-everything is sent to `failures`, which sends it back to `status`; `failures` is hinted with zero red cases | `TestsTool.cs:303-329` | hint `run wait=true` when stale is small and non-zero; no hint when red and stale are both zero |
| medium | Neither `impact` nor `edit` ever hands off to `tests`; the routing block has no rule that names `tests` | `ImpactTool.cs` has no `NextStepHint`; `CrossToolHandoff` has no tests entry | add the handoff when CT is enabled and likely tests are stale; add the rule beside rule 3 |
| medium | Starting on a stale-everything workspace schedules a drain of every case after a 5-minute cooldown, and nothing states the cost; the first run after a restart is the whole suite as ID lists | `CtIdleDrainPolicy.cs:50` | print `drain: in 4m32s will run N cases`; state the restart cost in the description |
| low | This repo's own `CLAUDE.md` Testing section sends agents to `dotnet test --filter` and never mentions the tests tool | | add the CT sentence to the Testing section |

### Guidance channels and skills

| Sev | Finding | Cause | Fix |
|---|---|---|---|
| high | The core spends 35% of its 1,900 chars on binding prose that names `GOLDFISH_WORKSPACE` and `primary`; seven of ten routing lines are two to four words | `MILLER_AGENT_INSTRUCTIONS.md:16-18` | cut the binding text to one clause; spend the space on routing lines |
| high | The hook ignores stdin `cwd` and makes every session call `workspace list` | `hooks/miller-session-hook.cjs:19-36` | inject the realpath of the cwd (or the computed display id) as rule 0 |
| high | Two skills show pipe-joined enum values the server rejects; two show bare `workspace` calls MCP refuses; `docs/agent-guidance.md` and `miller-orientation` say `depth=full` for callers although overview carries them (full is 60% of inspect calls and 70% of inspect tokens) | `.agents/skills/miller-explore-area/SKILL.md:51-56`, `miller-search-debug/SKILL.md:40-49`, `agent-guidance.md:125`, `miller-orientation/SKILL.md:80` | one value per example; add `workspace_id`; say overview for callers |
| medium | `allowed-tools` in 12 skills names `mcp__miller__*` but the plugin tools are `mcp__plugin_miller_miller__*`; skills use `arguments:` where the harness key is `argument-hint` | | fix the prefix and key; update `plugin-manifest.test.cjs` |
| medium | The stale-sidecar error is CLI-worded and carries no MCP next action; no skill triggers on it | `SymbolSearchSidecar.cs:299`; `ToolDiagnostic.cs:91-148` | typed exception mapped to `workspace(operation="refresh")`; harness-neutral text |
| medium | Codex gets no injection today and `install.md` says both that the hook runs and that Codex cannot load it; Cursor supports plugin hooks and Miller ships none | | state the Codex fallback plainly; add a Cursor `sessionStart` hook |
| low | `miller-orientation` restates the injected routing block as a table (5,654 chars) and 12 skills repeat a 593-char targeting block (13% of the skill corpus) | | keep the gotchas; one line for targeting |

## Corrected claims

Claims from the first two batches that verification narrowed. The corrected form is what the tables
above use.

- "Every edit waits a fixed 2.4 s" became "2.3 to 2.7 s on this 222,674-symbol index, 74 to 143 ms
  on a 5,318-symbol index"; the `wait_reason=index_load` stamp is on every tool and is not evidence.
- "68 reference(s) in the header" became "the header prints the page count (17); 68 appears only in
  JSON `nodes_visited` or the small-limit note".
- "The Miller-only arm spent 6x" became "one task and one agent choice caused 77% of the cost; without
  it the gap is 1.56x, and a single narrower call answers that task in 803 tokens".
- "4 exact-phrase test hits hidden" became "3 test chunks plus one misflagged production file hidden";
  the misflag is a real defect in the content-corpus writer.
- "363 of 625 edits followed by impact" counted previews; applied edits are 227, with 136 (60%)
  followed by impact and 3 (1.3%) by a tests run.
- "1,288 sidecar errors" counts two messages; the Search-sidecar message alone is 1,037.
- "prune refused 52 of 88" became "52 flagged as refusal, 40 of which still listed 30 to 33 prunable
  rows; 33 of the 39 dead rows are prunable today".
- "tests start returned in 0.15 s" was the daemon's own elapsed line; the tool call took 368 ms.
- "A 35 s ack on 2026-09-03" is not recorded anywhere and is dropped.
- "workspace refresh is a 2 s refusal on a reader" holds for a reader with a deferred primary (the
  plugin server); a reader with a bound primary refuses in 1 ms.

## Strengths that must not regress

- `inspect` accuracy: every symbol, line range, and constant value matched ground truth; body
  continuation is byte-exact; `Parent.Member` targets resolve.
- `trace` scoped refs and method-to-method paths are exact and carry enclosing symbols; JavaScript
  refs work.
- `impact` hop-1 precision on a real symbol is exact and excludes doc-comment mentions; the empty
  git-diff case is 43 tokens.
- `patterns` counts are parser-exact (6,140 headings outside fences where rg counts 6,202); all eight
  repo languages show facts; no-match answers are typed and honest.
- `edit` previews write nothing, and the match proof (rung, count, span, disk verified, index used or
  not) is honest; `anchor`, `line`, and `occurrence=all` resolve repeated literals the harness Edit
  refuses.
- `tests status` is cheap and side-effect free; `failures` is bounded; explicit runs travel as
  explicit ID lists.
- The routing block, `miller rules`, and the setup snippet are one byte-identical source; delivery to
  main sessions and subagents is proven.
- `workspace onboarding` names real hot targets and flows for this repo; `workspace list filter=`
  is exact and cheap.
- Telemetry is rich enough to audit every claim in this document from `~/.miller/telemetry.db`.

## Recommendations, ranked

Effort is stated for an AI coding agent. A task is one focused change with tests.

1. Freshness banner from evidence plus rule 6 rewording (1 task). Removes the most frequent
   contradiction an agent sees.
2. Impact compact: likely tests first, a continuation, tier flags, `via` as a name (1 to 2 tasks).
   Removes the largest token sink and the cause of the A/B gap.
3. CT loop: stamp the tick inside selection, publish the phase, fix the ack path, check graph
   truncation before the identifier read (2 to 3 tasks). Turns first contact from 15 minutes of
   silence into a status an agent can wait on.
4. Hook injects the cwd realpath as rule 0; move the `tests` sequence into a numbered rule; cut rules
   4, 5, and 8 to one clause each (1 task, plus one task for the core rewrite under the 1,900 cap).
5. Edit: skip the projection load for disk-only replaces and cache it by snapshot key (1 task);
   refuse a body that starts with the signature (small).
6. Overload references: attribute qualified call sites to every overload; stop suppressing fallback
   for an explicit id (1 task). Today a common refactor target has zero visible callers.
7. Content-corpus test flag: use `TestPathClassifier`; print hidden-row counts in source and content
   search (1 task).
8. Context disposition and `edited_files` callers (1 to 2 tasks).
9. Runner line per test project from the CT provider, in `tests status` and `impact` (1 task per
   provider family; parity applies).
10. Skills drift fixes and the `allowed-tools` prefix (small, mechanical).
11. Store retention and dead-row reclaim (julie-extract owns retention; Miller owns the registry and
    sidecars): surface physical bytes in health, delete the row and reclaim sidecars with an owed
    record when retire-view fails (2 tasks across two repos).
12. Trace compact rows: merge fallback duplicates, drop `site=` ids; bridge route lookup before the
    sentence (1 task).

Product decisions that need the owner: whether `tests` should be named in a numbered routing rule
ahead of `impact`; whether compact `impact` should page or reserve; whether the razorback toolchain
table should be retired in favor of Miller's block; and the retention policy for the family store.

## Reproduction

- Workflow scripts and per-agent journals live under the session directory
  `~/.claude/projects/-home-murphy-source-miller/01af7709-44a0-4fab-8996-6cf48fb08710/workflows/`.
- Telemetry queries used the `ts`, `tool`, `op`, `duration_ms`, `outcome`, `est_tokens`,
  `error_message`, and `metadata_json` columns of `tool_telemetry`; the workspace filter is
  `workspace_id LIKE '91250a0fd4f3%'`.
- CT timeline: `.miller/ct/daemon.status.json`, `.miller/ct/commands/7b06afab*.json`, role=ct lines
  in `.miller/logs/miller-20260907.jsonl`, and `dotnet-stack report -p <daemon pid>`.
- Disk: `du -sh ~/.miller/stores/*` and `sqlite3 ~/.miller/workspaces.db`.
