# Julie vs Miller Edit Adoption Investigation

Date: 2026-06-28

## Question

Why did Julie's edit tools get real usage while Miller's `edit` tool is barely used?

This is not a parity exercise. Miller should learn from Julie where Julie created a better agent workflow, then adapt the lesson into Miller's smaller, safer, Eros-ready tool surface.

## Data Sources

- Julie 90-day local usage summary from `/Users/murphy/source/julie/scripts/tool_usage_stats.py --days 90 --json`.
- Julie local log scan of `.julie/logs/julie.log*` for `edit_file`, `rewrite_symbol`, `edit_symbol`, and `rename_symbol`.
- Miller local telemetry from `~/.miller/telemetry.db`.
- Julie current server instructions, editing skill, tool descriptions, and edit tests.
- Miller current server instructions, editing skill, tool description, edit implementation, and edit tests.

Julie caveat: the current Julie `tool_calls` DB table has zero rows, so durable argument-level usage is unavailable from that table. The usable historical evidence is the CLI summary and log stream.

## Usage Snapshot

Julie 90-day tool usage:

| tool | calls | percent |
|---|---:|---:|
| `get_symbols` | 11,065 | 31.35% |
| `fast_search` | 10,798 | 30.60% |
| `deep_dive` | 5,940 | 16.83% |
| `edit_file` | 2,521 | 7.14% |
| `fast_refs` | 2,322 | 6.58% |
| `rewrite_symbol` | 108 | 0.31% |
| `edit_symbol` | 16 | 0.05% |
| `rename_symbol` | 2 | 0.01% |

Julie edit-family total: 2,647 / 35,293 calls, about 7.5%.

Miller telemetry:

| tool | calls | percent |
|---|---:|---:|
| `inspect` | 7,635 | 51.51% |
| `search` | 4,359 | 29.41% |
| `workspace` | 876 | 5.91% |
| `context` | 657 | 4.43% |
| `content` | 526 | 3.55% |
| `trace` | 391 | 2.64% |
| `impact` | 299 | 2.02% |
| `patterns` | 64 | 0.43% |
| `edit` | 14 | 0.09% |

Miller `edit` detail:

| op | calls | ok | empty | error | first | last |
|---|---:|---:|---:|---:|---|---|
| blank/unknown | 5 | 4 | 1 | 0 | 2026-06-06 | 2026-06-14 |
| `replace_text` | 9 | 6 | 0 | 3 | 2026-06-17 | 2026-06-28 |

No current Miller telemetry shows `replace_symbol_body`, `replace_symbol_signature`, `rename_symbol`, `insert_before`, `insert_after`, or `add_doc` being used.

## What Julie Did Better

### 1. Julie made edit_file the obvious default

Julie instructions say `edit_file` and `rewrite_symbol` are the default for file modifications. They explicitly frame Read + Edit as the fallback.

Julie editing skill trigger:

- Use before changing existing files.
- Trigger on fix, change, update, modify, refactor, rename, replace, add, remove, move, or any existing-file task.
- Route to Julie editing tools without a Read + Edit loop.

That is adoption-oriented guidance. It tells the agent when to use the tool, why it is cheaper, and what not to do.

Miller guidance is safer and more conservative:

- Search/inspect first.
- Run `impact` for public/shared changes.
- Preview with `edit`.
- Apply with `apply=true`.
- `replace_text` requires exact `old_text`.

That is correct, but it does not make `edit` feel like the default path. It makes `edit` feel like a specialized final step after the agent has already gathered enough exact text to use native patching.

### 2. Julie's high-use path was arbitrary text editing, not symbol refactoring

Julie edit-family usage is dominated by `edit_file`:

- `edit_file`: 2,521 calls.
- `rewrite_symbol`: 108 calls.
- `edit_symbol`: 16 calls.
- `rename_symbol`: 2 calls.

So the lesson is not mainly "agents need more symbol-aware edit operations." Miller already has those operations and safety gates. The lesson is that agents used Julie when it made small arbitrary text edits cheaper than the native file-edit loop.

### 3. Julie had fuzzy matching that reduced pre-read friction

Julie `edit_file` matches in phases:

1. Exact substring.
2. Trimmed-line matching for whitespace, indentation, and line ending tolerance.
3. DMP fuzzy matching for short patterns.

Tests cover indentation differences, trailing whitespace, tabs vs spaces, extra/missing characters, overlapping fuzzy spans, CRLF preservation, occurrence selection, and no-match errors.

Miller `replace_text` is literal ordinal matching. That is simple and safe, but it usually requires the agent to first inspect/read enough exact text to copy into `old_text`. Once the agent has exact text, native `apply_patch` is often just as easy or easier.

### 4. Julie exposed dedicated edit tool names

Julie exposed first-class `edit_file`, `rewrite_symbol`, and `rename_symbol` tools. Miller deliberately collapsed those into one `edit` tool with an `operation` enum.

Keeping Miller's MCP surface smaller is still the right instinct, but the adoption cost is real:

- `edit_file` advertises the common action directly in the tool name.
- `edit` requires the agent to know operation names.
- The most common Julie path maps to `edit(operation="replace_text", ...)`, which is less discoverable and less obviously better than patching.

### 5. Julie's parameter descriptions taught body-span expectations

Julie `rewrite_symbol` parameter docs explain what `replace_body` means for brace-delimited and indentation-delimited languages, and which declarations error. Miller's `edit` tool description lists operation names but does not carry the same operation-specific guidance in the parameter descriptions.

This probably matters less for adoption than `edit_file`, because Julie symbol-edit usage was small. It still affects confidence when an agent considers a symbol rewrite.

### 6. Julie had hooks, but they are not the main explanation

Julie has `.claude/hooks/pretool-edit.cjs` and `.claude/hooks/pretool-agent.cjs` nudging agents toward Julie tools. The edit hook message is:

```text
Use edit_file or rewrite_symbol instead -- they don't require reading the file first.
```

However, prior Julie docs note those hooks used raw `console.log` and may not have reached the model in Claude Code until later hook-output work. Treat hooks as supporting evidence, not the primary adoption cause.

## What Miller Already Does Better

Miller's edit implementation is safer than Julie's old convenience path in several important ways:

- Preview by default; writes require `apply=true`.
- Freshness gate on apply.
- Gate-time self-heal for stale targets.
- Replans symbol edits after recovery so stale byte spans are not applied.
- Atomic write path with TOCTOU re-check and rollback.
- Write-through convergence after apply.
- Works through Miller's cross-workspace selector model.

This means the goal should not be "clone Julie edit." The goal should be "make Miller's safe edit path compelling enough that agents choose it when it is actually better."

## Main Gap

Miller lacks a low-friction arbitrary text edit path that beats native patching.

For symbol operations, Miller has a good foundation. For the dominant Julie use case, Miller currently requires exact `old_text`. That turns `edit replace_text` into:

1. Search or inspect.
2. Copy exact text.
3. Preview edit.
4. Apply edit.

Native patching is usually:

1. Read/search.
2. Apply patch.

Julie won usage because `edit_file` let the agent skip exact pre-read in many small edits. Miller does not yet have an equivalent agent-visible advantage.

## Indexed Content Changes The Design Question

The original Miller `edit` design was written when Miller did not yet have the current content corpus. That history matters. The implemented tool is mostly an index-aware safe patch applier: it resolves symbols, reads current disk text internally, gates freshness, previews a diff, and applies atomically. That is useful, but it is not the same product as Julie's token-saving edit path.

The current data structures change what `edit` should be:

- `symbols.db` still does not store full file content; it stores file hashes, byte spans, and symbol/reference facts.
- `content.db` is now a revision-fresh sidecar built from workspace files and stores `raw_text` chunks with byte ranges, source hashes, and source metadata.
- The content corpus is chunked and normalized for search/read, not currently a clean per-file edit buffer.
- The writer skips some workspace files: too large files, missing files, hash mismatches, non-decodable files, and I/O failures.
- `IndexedSourceTextReader` explicitly says the corpus is advisory and "not an edit buffer" today.

So the user's premise is directionally right but needs one correction: Miller does not yet have a first-class "current full file edit snapshot" API. It does have enough indexed text infrastructure to build one, or to build an edit selector that uses indexed content to locate candidate spans before applying against disk with the existing freshness and TOCTOU checks.

That means the current `edit` tool is probably the wrong shape for the product goal. It was designed around safety and symbol spans. It was not designed around the highest-value Julie workflow: make a tiny edit without forcing the agent to read the whole file into context in harnesses where the native edit tool requires a prior full Read.

## Remove vs Redesign

Deletion test:

- If Miller deletes `edit` today, most harnesses still have a way to edit files, and agents lose very little current practical behavior because `edit` usage is near zero.
- But deleting it also gives up the one cross-harness capability Miller can uniquely provide: indexed, freshness-aware, token-saving edits that do not require the model to ingest a full file.
- The current implementation already has valuable safety pieces: preview, stale gate, gate-time recovery, replan-after-recovery, atomic write, TOCTOU re-check, rollback, and post-apply convergence.

So removal is the wrong first move. The correct move is redesign. If the redesigned interface still cannot make a common edit cheaper or safer than built-in harness editing, then removal should come back on the table.

Architecture framing:

- Current shallow interface: caller supplies exact `old_text`, so Miller mostly hides only atomic apply mechanics.
- Desired deeper interface: caller supplies an edit intent and a small selector, while Miller uses indexed content to find candidate spans, previews the exact diff, and applies against verified current disk content.
- Caller-facing win: the model should not need a full-file Read for a one-character or one-line edit.

The edit tool should earn its place by doing something built-in edit tools generally do not do:

1. Use the current index/content corpus to locate candidate edit spans from small selectors.
2. Avoid dumping full files into the model context.
3. Prove the edit with a concise preview before writing.
4. Apply only after verifying the disk file still matches the indexed/found content.
5. Reindex/converge after writing.

## Recommendations

### 1. Redesign Miller `edit` around token-saving indexed-content edits

Keep the MCP tool count unchanged at first. Change the existing `edit` tool so `replace_text` is no longer just an exact-text patch operation.

The redesigned file-text path should support:

- `match_mode=auto|exact|normalized|fuzzy`, default `auto`.
- `auto` tries exact, normalized line matching, then bounded fuzzy matching.
- Indexed-content candidate discovery before disk apply.
- A small selector surface, such as `old_text`, `anchor`, `line`, or `query`, without requiring a full file read.
- Preview output must state the match mode used.
- JSON output should include `match_mode`, match count, selected spans, and whether the candidate came from indexed content or disk verification.
- Apply path must keep current freshness and TOCTOU guarantees.

This adapts Julie's biggest adoption driver without adding a new MCP tool or weakening Miller's safety model.

### 2. Add a real edit-buffer/read layer if needed

Do not treat today's overlapped content chunks as an edit buffer by accident. Either:

- add a small `IndexedFileTextReader` that reconstructs a file from active content chunks only when coverage is complete and hash/revision facts match; or
- keep indexed content strictly as candidate discovery, then read disk internally and verify hash before planning/apply.

The second path is probably simpler and safer for the first redesign slice. The model still avoids reading the full file, while Miller's process can read disk internally for exact verification and diff generation.

### 3. Improve `edit` operation guidance without overpromising

Update:

- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- `EditTool` description and parameter descriptions.
- `.agents/skills/miller-editing/SKILL.md` and generated skill mirror.
- README / GitHub Pages edit copy if present.

Guidance should say:

- Use `edit` when Miller can make the edit safer or cheaper than native patching, especially when a harness would require a full-file Read before editing.
- Prefer `replace_text` for docs/config/arbitrary text when the target text is known or can be located by Miller's indexed-content selector.
- Prefer symbol operations when changing a symbol span, signature, docs, or workspace rename.
- Use `apply_patch` for broad handcrafted multi-hunk edits where Miller adds no value.

This should not fight Codex's global `apply_patch` rule. It should carve out cases where Miller edit is objectively better.

### 4. Make `edit` previews more useful as evidence

Preview output should show:

- Match mode.
- Occurrence choice and number of matches found.
- Whether the match was found through indexed content and verified against current disk text.
- Whether the target was fresh or whether preview skipped the write-time freshness gate.
- For symbol body/signature operations, a short note about body span semantics when relevant.

The output should help an agent confidently re-call with `apply=true`.

### 5. Add a focused adoption matrix for edit

Add benchmark rows that reflect real Julie-winning workflows:

- Change a version string in TOML/JSON/Markdown without reading the whole file.
- Update an indented line where old text has different indentation.
- Replace text with trailing whitespace/CRLF differences.
- Make a short typo-tolerant replacement.
- Replace a symbol body after `inspect depth=overview`.
- Rename a symbol and verify preview warns about name-based matching/homonyms if still applicable.

Measure:

- Tool calls needed.
- Empty/error rate.
- Preview clarity.
- Whether apply changed exactly the intended bytes.

### 6. Defer new edit tools

Do not add `edit_file` as a separate MCP tool by default. The Miller product rule is to keep MCP surface stingy. Try making the existing `edit` operation path better first.

Reconsider a dedicated alias only if data shows agents still do not discover the common `replace_text` path after fuzzy matching and guidance updates.

## Proposed Next Slice

Build "edit adoption hardening" as a focused implementation slice:

1. Redesign `edit replace_text` as an indexed-content-assisted, token-saving edit operation.
2. Add `match_mode` support to `edit replace_text`.
3. Port Julie's normalized/fuzzy matching behavior into Miller Core with tighter bounds and clear preview metadata.
4. Use indexed content to find candidate spans when the agent supplies a small selector, then verify against current disk text before planning/apply.
5. Keep the existing freshness/atomic apply guarantees.
6. Update server instructions, tool descriptions, Miller editing skill, README/GitHub Pages as needed.
7. Add edit adoption matrix rows and xUnit coverage.
8. Verify with `scripts/test.sh` plus targeted matrix rows.

Expected outcome: Miller keeps the smaller, safer `edit` surface, but gains the one behavior that made Julie's edit path materially more attractive than native file patching.

## Implementation Status

Implemented 2026-06-28 as the token-saving `edit replace_text` redesign.

What changed:

- `replace_text` now supports `match_mode=auto|exact|normalized|fuzzy`.
- `query`, `anchor`, and `line` selectors can narrow indexed content candidates before Miller verifies the match against current disk text.
- Preview/apply output includes match proof: mode, source, line range, match count, occurrence, disk verification, and content index state.
- Existing safety behavior stays intact: preview by default, freshness gate, TOCTOU re-check, rollback, and write-through convergence.
- Server instructions, tool descriptions, Miller skills, README, and GitHub Pages now explain when `edit` is the token-saving path.
- Foundation matrix rows hard-gate exact version preview, normalized indentation, trailing whitespace, bounded fuzzy matching, line and anchor duplicate disambiguation, selector no-match guidance, and output ceilings.

Evidence:

- Matrix summary: `docs/findings/benchmarks/2026-06-28-token-saving-edit-redesign/summary.md`
- Matrix CSV: `docs/findings/benchmarks/2026-06-28-token-saving-edit-redesign/results.csv`
- Matrix JSON: `docs/findings/benchmarks/2026-06-28-token-saving-edit-redesign/results.json`

Interpretation boundary: the matrix rows are hard-gate behavior evidence. Julie/Miller usage and adoption telemetry remain report-only inputs for product direction, not pass/fail release criteria.
