# M9 design — the ad-hoc big-file tool (julie-parses, Miller segregates + merges the UX)

> Historical status: this design was a precursor to the implemented content corpus. Current file/content text
> behavior is documented in [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md), [`README.md`](../README.md),
> and the `miller-large-file`, `miller-web-research`, and `miller-text-audit` skills.

Status: **PARKED — spec complete, build decision deferred.** The architecture is sound and v1 is buildable now
(no julie change for v1). What is NOT yet decided is whether it is **worth building** — that hinges on one
unmeasured cost (see "Viability" + "Gating experiments"). Revisit after thinking it through. Grounded against the
live pinned `julie-server` v7.12.2 (schema 26). Confidence ~85 on the architecture; the VALUE is conditional, not
assumed.

## The idea (restated)

An agent often needs to read a **big file that is not workspace code** — a log, a JSON dump, a giant markdown
doc, possibly **outside the workspace tree**. Today its options are `Read` (token-bomb) or the shell
(`tail`/`grep`/`rg`/`jq`/`awk`). Miller's whole thesis is "make file-reading token-efficient — use this before
cat/grep/tail." This tool extends that thesis from code to **arbitrary large files**: surface
**search / read / stream / facet** operations that return *spans, counts, and facets* instead of raw dumps, with a
**hard bound** on output size.

**Design principle (your call):** parsing stays in **julie** for consistency — Miller does not grow its own
parser. Miller's job is (1) the **segregation architecture** (ad-hoc data must never pollute the workspace index)
and (2) the **merged UX**. (The Viability section below pressure-tests whether that principle should bend for the
log case.)

---

## Viability vs the shell — the honest assessment (added 2026-05-30 after a pressure-test)

The framing "M9 vs tail" is too generous to M9. An AI agent has the **whole Bash toolbox** — `grep`, `rg`, `jq`,
`awk`, `sort | uniq -c`, `sed -n`, `wc`. That is the real competitor, a much higher bar than tail. Notably, the
"killer facets" pitched below (error-signature rollup) is just `grep ERROR | sort | uniq -c | sort -rn` — a
competent agent already writes that in one pipe. So the original pitch oversold the differentiator. The honest
breakdown:

### Where M9 genuinely wins (the real value)
- **Structurally bounded output (the strongest argument).** A human runs `grep` and stops scrolling; an **agent
  ingests every byte of stdout into its context, irreversibly, at token cost.** `grep flock huge.log` can dump
  240K tokens before the agent knows what hit it. A tool that *guarantees* "≤N tokens + a cursor for more" removes
  a footgun the shell structurally cannot. Same reason Miller's code-search thesis holds — it is consistent.
- **Repeated queries amortize.** Debugging = ~10 queries against the same large file. Shell rescans every time;
  M9 indexes once. Real win for interactive sessions (and only there).
- **Typed interface, fewer failed round-trips.** Agents botch regexes and `sed` ranges; each miss is a round
  trip. Typed `search/read/facets` is harder to get wrong.
- **Unfamiliar-JSON orientation.** `open`'s cheap shape summary beats `jq`, which requires you to already know the
  paths.

### Where the shell wins (decisively, on its turf)
- **One-shot peeks.** "What's the last error?" → `tail -50 | grep ERROR` is one instant command, zero ingest.
  M9 must extract → copy → index first. **For one-shots M9 loses outright;** it only pays off on repeat or on
  bounding.
- **A disciplined agent narrows the edge.** `grep -c` then `grep | head` is bounded too. The remaining win is
  "M9 makes the safe/bounded path the DEFAULT path" — valuable precisely because Miller targets *unknown* agents
  you can't assume are disciplined. For a careful operator the marginal value is lower; for a product, higher.

### The make-or-break unknown (UNMEASURED — only tiny files tested so far)
- **Ingest cost on a genuinely big file.** julie's text fallback stores **full content into SQLite** — a complete
  copy. For a 2GB log that is 2GB+ read + 2GB+ written to `~/.miller/adhoc/<sha>.db`, plus any FTS build:
  plausibly seconds-to-minutes, vs tail's milliseconds. **If ingest is slow, the value band collapses** to
  "medium files queried repeatedly," which may not justify the build. This is the gate; measure before committing.
- **Search engine is not free reuse.** Miller's existing search is over **symbols**, and logs/text have **zero
  symbols** — so the `search` op is likely **new content-FTS work**, not the cheap reuse the Components section
  implies. Verify what content search Miller actually has today before scoping `search`.

### The sharpest design implication — a fork for logs
For **logs/raw text specifically, julie ingest may be the wrong engine.** There is nothing to parse — it is
bounded line access. A **direct streaming line-reader** (memory-mapped, line-indexed, bounded grep with cursors)
would beat the shell on the exact axis that matters (bounded output) with **zero ingest cost and no file-size
ceiling**, sidestepping the 2GB-copy-into-SQLite problem entirely. That forks the tool:
- **structured (json/yaml/md/code) → julie ingest** (you want symbols + content).
- **raw text/logs → direct streaming reader** (you want instant bounded slices, no copy).

This bends "parsing stays in julie" — but a line reader is not a parser. It is the difference between "beats the
shell sometimes" and "beats the shell always, for logs." **Open decision for you.**

### Viability verdict
- **Viable: yes. High-value: conditional.** The bounded-output-for-agents core is genuinely worth building; the
  *julie-ingest mechanism for logs* is the part to distrust until measured, and it may lose to a simpler reader.
- The value is highest because Miller serves unknown agents (can't assume disciplined `grep`). For you driving a
  capable agent, the marginal value is lower — weigh that when deciding whether to spend the build.

---

## Verified facts (re-probed live, schema 26, 2026-05-30)

### julie's CLI gives us the segregation lever
`extract --db <DB> --root <ROOT> {scan|update|delete|info}`. The `--db` is arbitrary — **a different `--db` is a
fully separate index.** That is the segregation mechanism: ad-hoc files extract into their OWN db, never the
workspace `symbols.db`.

### julie's format coverage (28 grammar extractors + a text fallback)
Grammar extractors (rich symbols): bash, c, cpp, csharp, css, dart, gdscript, go, **html**, java, javascript,
kotlin, php, powershell, python, razor, regex, ruby, rust, **sql**, swift, typescript, vue, zig, lua,
**markdown**, **json**, **yaml**.

### THE STORAGE BEHAVIOR — re-probed (this replaced an earlier misread)
Scanning a mixed ad-hoc dir, what julie actually stores per file:

| file | `files.content` stored? | symbols | language | note |
|---|---|---|---|---|
| `c.json` | ✅ | yes | `json` | grammar extractor |
| `notes.md` | ✅ | yes | `markdown` | grammar extractor |
| `cfg.yaml` | ✅ | yes | `yaml` | grammar extractor |
| `data.csv` | ✅ 14 B | 0 | **`text`** | **text fallback stores full content** |
| `notes.txt` | ✅ 39 B | 0 | **`text`** | text fallback |
| `stream.ndjson` | ✅ 30 B | 0 | **`text`** | text fallback |
| `daemon_noext` (no ext) | ✅ 38 B | 0 | **`text`** | text fallback |
| **`daemon.log`** | ❌ **absent (not scanned)** | — | — | **`*.log` is default-IGNORED** |

Two corrected facts that drive the design:
1. **julie has a `text` fallback** — for unknown-but-allowed extensions it stores the **full raw content**
   (language `text`, 0 symbols). So content-FTS + byte/line-slice works for almost any file **today**.
2. **`.log` is in julie's DEFAULT ignore set** (silently skipped; an `--ignore-file` only ADDS patterns). This —
   NOT a lack of content storage — is the only thing between the tool and the headline log use case.

### The `.log` ignore is sidesteppable on the MILLER side (proven)
Byte-identical content under four names, one scan: `daemon.log.txt`, `daemon_noext`, `daemon.text` all stored
(38 B, `text`); only literal `daemon.log` dropped. **So Miller's ingest names the temp copy with a non-ignored
suffix** → julie's text fallback stores the log's full content. The log case is NOT julie-blocked for tier-1.
(Note: this only matters if the julie-ingest engine wins the Viability fork; a direct streaming reader needs no
rename trick.)

---

## What needs julie vs what is buildable now

- **Buildable now (no julie change):**
  - **Track A (rich):** json, yaml, markdown, html, sql, all code langs — content + symbols.
  - **Track Text (content-only):** csv, txt, ndjson, no-ext, **and logs via the D2 temp-rename** — full content,
    so **search (FTS) + read/stream (byte-line slice) + line-grouped facets** all work.
- **Wants julie later (tier-2, premium only):** a **log record extractor** framing a log into records with
  **fields** (timestamp, level, logger, message; multi-line stack traces as one record). Unlocks rich facets
  (level histogram, time-range, error-signature rollups). **Open question for you:** julie already ships a `regex`
  extractor + `bash`/`powershell` — does that machinery generalize to a log/ndjson/csv record extractor, or is it
  a new extractor kind? This is the ONLY real julie dependency, a v2 enhancement into the SAME surface — it does
  not block v1.

---

## Decisions (Miller side — the architecture + UX)

### D1 — Segregation: a separate ad-hoc index per file, never the workspace
- Each ad-hoc file lives in its OWN store: `~/.miller/adhoc/<sha256(canonical_path)>.db` (a **user-global** cache,
  not `<workspace>/.miller/`, because the file may be **outside any workspace** and may be opened from several
  workspaces). Keyed by the file's canonical path hash so re-opening reuses it.
- **Never** read into the workspace `MillerRepositoryIndex`; **never** watched by `IndexerService`; **never**
  polled by `FreshnessService`. Physically separate. Hard guarantee: ad-hoc data cannot leak into
  search/inspect/impact results.
- **Ephemeral + bounded:** an LRU/size cap over `~/.miller/adhoc/` (evict least-recently-opened over N files /
  M bytes). A pure `AdhocCacheReaper.Plan` (mirrors M8's `LogFileReaper`) decides evictions; thin infra deletes.
  Re-open re-ingests if evicted.
- **NOTE (per Viability fork):** if logs go through the streaming-reader path, "the store" for a log is a
  lightweight **line index**, not a julie db — same segregation rules, different backing artifact.

### D2 — Ingestion mechanic: extract exactly ONE file (julie path) (+ the .log workaround)
julie's first call on a fresh db must be a `scan` (binds workspace_id + root) over a whole `--root` dir. To ingest
exactly one ad-hoc file, and to dodge the `.log` default-ignore:
- **Temp-dir-of-one with an ingest-safe name.** Temp dir, hardlink (copy if cross-device) the target in under a
  name julie will NOT ignore (drop the `.log` extension), keep the original name for display, `scan --root
  <tempdir> --db <adhocdb>`, drop the temp dir.
- Preserve the real display path in the handle (never show the agent `daemon.log.txt`).
- Canonical-path discipline (symlink-resolved) applies.
- **VERIFY AT BUILD TIME (and BEFORE committing to this engine — see Gating):** content present for Track A +
  Track Text incl. a renamed `.log`; re-`scan` on an unchanged file is a no-op; **ingest TIME + DISK on a large
  (500MB–2GB) file.**

### D3 — Surface: a dedicated `file` tool, distinct from the 7 workspace tools (merged UX, not merged data)
A new MCP tool so ad-hoc results never bleed into code results, with a consistent feel (smart-string target,
compact/json, the rg/grep/cat steer). Operations:

| op | purpose | returns (token-thrifty) |
|---|---|---|
| `open` (default) | summarize a path (the handle) | handling-tier, size, line count, json: top-level shape; the handle |
| `search` | query within the file | matching lines/records with line+byte anchors + a few context lines + a total-match count (not all matches) |
| `read` | a line/byte/record range (paged) | the slice + a next-cursor; never the whole file |
| `facets` | structured rollup | tier-text: distinct line signatures with counts + first/last line, collapsed repeats; json: key/path frequency; (rich log level/time facets land with julie tier-2) |
| `stream`/`tail` | last N lines/records, optional follow | the tail window + a cursor; **live-follow deferred (D6)** |

- `target` is a file path (in or out of workspace). The tool resolves it to (or creates) its handle.
- **The token win vs the shell is the BOUND, not the facet** (the facet is a one-liner in shell). Every op returns
  a capped payload + a cursor — that guarantee is the product.

### D4 — Reuse, don't reinvent (caveat the search claim)
- Reading slices: `ExtractReader.ReadBody` already slices `files.content` by **byte or line** span (UTF-8 safe) —
  the `read`/`stream` engine for the julie path.
- Search: **VERIFY** what content search Miller has today — its index is symbol-oriented and text files have zero
  symbols, so `search` may be NEW content-FTS work, not free reuse. Do not assume.
- Telemetry: the `file` tool rides the same central filter + ledger (its own tool row; `bytes_returned` is THE KPI
  — the entire reason this tool exists is shrinking that number vs `Read`).
- Smart-string + compact/json + the description steer: mirror the existing tools.

### D5 — Honesty about handling tier (no faked capability)
`open` reports the detected **handling tier**:
- **structured** (json/yaml/md/code) — full navigation + symbols.
- **text** (csv/txt/ndjson/log-via-reader/no-ext) — search + slice + line-signature facets (no field-level log
  facets yet).
- **rich-log** — only once julie tier-2 lands; until then a `.log` is handled as **text** (truthfully labeled),
  never pretended to be field-parsed.

### D6 — Explicit scope fences
- **v1 = static ingest + open/search/read/facets** over Track A + Track Text (incl. logs).
- **Live-tail/follow** (growing file, file handles, watcher) — the riskiest piece — **deferred to v2.**
- **Semantic/embedding** search is OUT (no embeddings in the default path). Prose big-files get FTS + chunk
  windows only.
- **Rich log field facets** (level/time/error-signature-by-field) depend on julie tier-2.

---

## Gating experiments — DO THESE BEFORE committing to build (the decision inputs)
1. **Measure julie ingest** on a 500MB–2GB real log: wall-clock + peak disk for the temp-dir-of-one scan into
   `~/.miller/adhoc/`. **This is the primary gate.** Cheap ingest → julie path is fine. Heavy ingest → the log
   path should be a streaming reader.
2. **Prototype the direct streaming line-reader** (mmap + line index + bounded grep with cursors) and compare it
   head-to-head with the julie-ingest path on the same big log: first-query latency, repeat-query latency, memory,
   max file size. Decides the Viability fork.
3. **Verify Miller's existing content search** — is there any FTS over `files.content` today, or only symbol
   search? Determines whether `search` is reuse or new work, and thus the true build cost.
4. **Sanity-check the value for the actual user** — would a capable agent driving Miller reach for `file` over a
   one-line `grep | head`? If the honest answer is "rarely," the bound-by-default argument has to carry the whole
   tool; decide if that is enough.

---

## Components (when/if built — costs depend on the fork)
- **Miller.Indexing:** `AdhocExtractor` (temp-dir-of-one scan, julie path) **and/or** `StreamingLineReader`
  (mmap + line index, log/text path); `AdhocIndexStore` (open/resolve a per-file handle by path hash);
  `AdhocCacheReaper` (pure eviction planner + thin delete).
- **Miller.Core:** pure helpers — line-signature facet rollup (collapse + count + first/last line), json shape
  summary, paging cursor math. (All pure, unit-tested on in-memory fixtures.)
- **Miller.Server:** `FileTool` (`[McpServerTool(Name="file")]`, the 4–5 ops), smart path resolution, compact/json
  renderers, telemetry.
- **Reused:** `ExtractReader.ReadBody`/byte-line slicing, `SqliteReadOnlyAccess`, `JulieExtractRunner`,
  `PathCanonicalizer`, the central telemetry filter. (`search` reuse is UNCONFIRMED — see D4/Gating-3.)

## Open questions to resolve before building
1. **The Viability fork (the big one):** julie-ingest for everything, or julie for structured + a streaming reader
   for logs/text? Gated by experiments 1–2.
2. **Single-file ingest details (julie path):** temp-dir-of-one + ingest-safe-name; rename rule (strip extension —
   simpler, proven).
3. **Cache location + bounds:** `~/.miller/adhoc/` global cache; N-files / M-bytes LRU caps.
4. **Path safety:** the tool reads arbitrary agent-named paths (read-only, lower risk, but name it) — any
   allow/deny policy for paths outside the workspace?
5. **(v2, not a v1 blocker) julie tier-2 log record extractor:** does julie's `regex`/line machinery generalize,
   or is it a new extractor kind? — needs you / julie; gates only the premium field-facets.

## Why "polish then spec" was the right order (the dogfood loop)
M8 adds a **JSONL log sink** to Miller's own logs — themselves a big structured file. So the **first customer of
this `file` tool is Miller's own `.jsonl` logs** (`file(".../miller-1234.jsonl", op=facets)` — debug Miller with
Miller). `.jsonl` is Track Text today (full content stored, line-sliceable); rich per-field facets arrive with
julie tier-2. That synergy is the argument for sequencing M8 first, which we did.

## Recommendation (revised after the pressure-test)
1. Land M8 (in flight) — produces the JSONL substrate regardless of M9's fate.
2. **Do NOT build M9 yet.** Run gating experiments 1–3 first (cheap: a few hours). They convert "conditional
   value" into a yes/no.
3. **Then decide the fork.** Cheap julie ingest → build v1 as spec'd (julie path for all tracks). Heavy ingest →
   build the streaming-reader as the log/text engine and reserve julie ingest for structured files — this is the
   stronger tail-killer and the likelier real product.
4. Defer live-tail (v2) and rich log field facets (julie tier-2) either way.

## Explicitly NOT in M9
- Miller-side *parsing* (parsing stays in julie — your decision; a line reader is not parsing). Live-tail/follow
  (v2). Embeddings/semantic. A multi-file ad-hoc "project" (one file per handle for v1). Editing ad-hoc files
  (read-only tool).
