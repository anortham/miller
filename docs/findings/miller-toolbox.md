# Miller toolbox design (2026-05-29)

Status: **agreed** with the user. The MCP tool surface for Miller (the .NET personal/local tier). Grounded in
the audit of julie/eros/codesearch tool designs ([[julie-eros-audit]]) and julie's real usage telemetry (below).
Confidence ~88.

## Why this shape (the telemetry that drove it)

julie's own tool-breakdown over ~20,890 calls:

| julie tool | calls | % | avg | p95 | signal |
|---|---|---|---|---|---|
| fast_search | 8,266 | 40% | 68ms | 174ms | the front door |
| get_symbols | 6,076 | 29% | ~12ms | fast | list-a-file is constant |
| deep_dive | 2,695 | 13% | 124ms | 262ms | symbol-anchored inspect |
| edit_file | 1,418 | 7% | 2ms | 5ms | — |
| fast_refs | 1,032 | 5% | 115ms | 229ms | — |
| get_context | 768 | 4% | **439ms** | **1216ms** | slow + lightly used |
| manage_workspace | 464 | 2% | (indexing) | — | admin |
| blast_radius | 171 | **0.8%** | **1274ms** | **5069ms** | slow + nearly unused |

Three findings, each a design mandate:
1. **search + get_symbols + deep_dive = 82% of calls.** These must be the simplest, fastest, best-documented tools.
2. **deep_dive (2,695) is symbol-anchored and fast; get_context (768) is task-anchored and 6x slower.** They are
   different intents, not one tool. deep_dive folds into `inspect`; get_context becomes its own `context` tool.
3. **The two slowest tools are the two least-used.** Speed drives adoption; blast_radius at 5s p95 is effectively
   dead. So in Miller, `context` and `impact` MUST be fast or they will be vestigial. This is the founding thesis
   (agents route around slow tools) confirmed by telemetry.

## Design principles

1. **7 tools by area.** Few enough to not overwhelm tool-selection; each a distinct, consistent intent.
2. **Mode/operation param only where sub-behaviors genuinely differ** (`edit`, `workspace`), and the **most-common
   op is the default** so the simplest call needs no mode decision. (Fixes codesearch's mandatory-`operation` tax.)
3. **Smart-string targets, never JSON-object/dict selectors.** A single `target`/`query` string, server-inferred,
   with an optional explicit override for the rare ambiguous case. (User confirmed from experience: dict/object
   params cause agent confusion and minor mistakes that cost extra tool calls. eros had to build server-side
   dict-key dispatch to compensate.)
4. **Single-required-arg 80% path** for every read tool.
5. **Preview-first mutation**: `edit` previews a diff by default and never writes unless `apply=true`.
6. **Freshness gate**: `edit` refuses a stale target (re-index first), with an `allow_stale` escape (eros pattern).
7. **`format=compact` default, `json` opt-in for chaining** — token economy on high-volume tools.
8. **Descriptions carry the steer** *"Use this before shell rg/grep/cat or reading whole files."* on every read
   tool — the load-bearing line that wins tool-selection against the agent's grep reflex (the product's reason to exist).
9. **Telemetry on every call from day 1** (see last section).

## Target resolution (the smart-string spec)

Read tools take a `target` (or `from`) string and infer its kind, because symbol IDs are opaque MD5s the agent
never types ([[julie-eros-audit]] §2) — the agent types a **name** or a **path**, which are trivially distinguished:

| input shape | resolved as | rule |
|---|---|---|
| contains `/` or a file extension (`.cs`, `.ts`, ...) | file path | path markers present |
| matches the id shape (hex/`::`/`file_` prefix) | symbol id (from a prior call) | use directly, no search |
| otherwise | symbol name | resolve via name lookup; if >1 match, return the candidates to disambiguate |

Optional overrides for the rare collision: `scope="src/Auth.cs"` (constrain a name to a file) and `as="symbol"|"file"`
(force the kind). 95%+ of calls need neither. codesearch's `NavigationTool.ResolveSymbolId` already does this
inference; Miller formalizes it + adds the overrides.

---

## The 7 tools

### 1. `search` — find code
> **Description (draft):** "Search indexed code and return ranked results. Use this before shell rg/grep/cat or
> reading whole files. Pass a symbol name, an identifier, or a natural-language phrase. Test code is hidden for
> natural-language queries unless you ask for it. Returns compact text by default; pass format=json to chain results."

| param | req | default | type | notes |
|---|---|---|---|---|
| `query` | ✅ | — | string | name, identifier, or phrase |
| `mode` | | `auto` | `auto\|text\|symbol\|file` | auto infers symbol-like vs phrase |
| `limit` | | `10` | int | |
| `exclude_tests` | | `null` | bool? | tri-state: null = hide for NL queries unless test/def intent; true/false = force |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `search("retry handler")`. (No semantic mode in the default path — lexical-first; semantic stays out unless ever added opt-in.)

### 2. `inspect` — view a known file or symbol (absorbs julie get_symbols + deep_dive, ~44% of calls)
> **Description (draft):** "Inspect a file or symbol you can already name. Give a file path to list its symbols, or
> a symbol name to see its definition, signature, and docs. Add depth=full to also get references, callers/callees,
> and the body. Use this before reading an entire file."

| param | req | default | type | notes |
|---|---|---|---|---|
| `target` | ✅ | — | string | smart-resolved path or symbol |
| `depth` | | `summary` | `summary\|full` | summary = file's symbols, or def+sig+doc; full = + refs, callers/callees, body, children (= deep_dive) |
| `kind` | | `null` | string? | filter when listing a file's symbols (function/class/...) |
| `scope` | | `null` | string? | disambiguate an ambiguous symbol name to a file |
| `limit` | | `50` | int | for file listing |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `inspect("AuthMiddleware")` or `inspect("src/Auth.cs")`.
**Token note:** julie's get_symbols returned the most data of any tool (21.6 MB). This is the heaviest token
consumer, so the compact default and `limit` matter most here.

### 3. `context` — task-anchored, token-budgeted bundle (absorbs julie get_context)
> **Description (draft):** "Assemble a token-budgeted bundle of the most relevant code for a task or question. Give
> a description of what you're working on — optionally a failing test or stack trace — and get a bounded set of the
> most relevant symbols and signatures with provenance. Use for orientation in an unfamiliar area; if you already
> know the symbol, use inspect."

| param | req | default | type | notes |
|---|---|---|---|---|
| `query` | ✅ | — | string | the task/question |
| `token_budget` | | `4000` | int | bound on returned size |
| `max_hops` | | `1` | int (0-2) | graph expansion |
| `entry_symbols` | | `null` | string[]? | seed symbols |
| `failing_test` | | `null` | string? | scenario hint (mode-switch without a mode enum) |
| `stack_trace` | | `null` | string? | scenario hint |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `context("how does login work")`.
**Perf mandate:** julie's was 439ms avg / 1.2s p95 and lightly used. Miller target: sub-100ms via the in-memory
index, or agents will skip it.

### 4. `trace` — follow edges (THE DIFFERENTIATOR; absorbs fast_refs + call_path + cross-language bridge)
> **Description (draft):** "Trace relationships from a symbol: where it is referenced, what it calls and what calls
> it, the shortest path between two symbols, or cross-language correspondences (e.g. a C# DTO to its TypeScript type
> to its database table). Use this instead of grepping for usages."

| param | req | default | type | notes |
|---|---|---|---|---|
| `from` | ✅ | — | string | smart-resolved symbol |
| `to` | | `null` | string? | if set → shortest path / reachability from→to |
| `mode` | | `auto` | `auto\|refs\|callers\|callees\|path\|bridge` | auto = refs + callers + callees; bridge = cross-language |
| `max_hops` | | `8` | int | for path |
| `limit` | | `50` | int | |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `trace("handleLogin")` → usages + callers + callees. `mode=bridge` is the unique cross-language capability.

### 5. `impact` — change-safety / blast radius (absorbs blast_radius)
> **Description (draft):** "Show what a change would affect — the symbols and tests downstream of editing a symbol
> or file. Use before a refactor, or to find which tests to run for a change."

| param | req | default | type | notes |
|---|---|---|---|---|
| `target` | | `null` | string? | symbol or file (smart-resolved) |
| `changed_paths` | | `null` | string[]? | or a set of changed files |
| `diff` | | `null` | string? | or a unified diff |
| `max_depth` | | `2` | int | |
| `limit` | | `100` | int | |
| `format` | | `compact` | `compact\|json` | (one of target/changed_paths/diff required) |

**80% call:** `impact("OrderService")`.
**Perf mandate:** julie's blast_radius was 1.3s avg / 5s p95 and 0.8% usage (effectively dead) because it computes
reachability on demand. Miller must keep a **precomputed transitive-closure** in the in-memory index so impact is fast.

### 6. `edit` — index-aware, preview-first, freshness-gated (absorbs edit_file + rename_symbol + rewrite_symbol)
> **Description (draft):** "Edit code with index awareness. Previews a diff by default and does NOT write; set
> apply=true to commit. Operations cover text replace, symbol body/signature rewrite, workspace-wide rename, insert,
> and doc add. Blocked if the index is stale for the target file — re-index first (or pass allow_stale)."

| param | req | default | type | notes |
|---|---|---|---|---|
| `operation` | ✅ | — | enum | replace_text, replace_symbol_body, replace_symbol_signature, rename_symbol, insert_before, insert_after, add_doc |
| `target` | ✅ | — | string | file + symbol (smart-resolved) |
| `old_text` | | `null` | string? | for replace_text |
| `new_text` | | `null` | string? | replacement / new name |
| `occurrence` | | `first` | `first\|last\|all` | |
| `apply` | | `false` | bool | must flip true to write; default previews a diff and writes nothing |
| `allow_stale` | | `false` | bool | bypass freshness gate |
| `scope` | | `null` | string? | disambiguate an ambiguous symbol name to a file (the cross-tool §2 override) |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `edit("replace_symbol_body", "OrderService.Process", new_text="...")` → returns a diff preview; re-call
with `apply=true` to commit. Mutation gate per eros's freshness design.

### 7. `workspace` — admin / index (absorbs manage_workspace)
> **Description (draft):** "Manage the workspace index. Defaults to status. Use refresh to update stale files, full
> to rebuild from scratch, list to see registered workspaces."

| param | req | default | type | notes |
|---|---|---|---|---|
| `operation` | | `status` | `status\|refresh\|full\|list\|open\|remove` | defaults to status |
| `path` | | `null` | string? | for refresh/full/open |
| `format` | | `compact` | `compact\|json` | |

**80% call:** `workspace()` → status.

---

## Shared conventions
- Every read tool: a single required `query`/`target`/`from` string, `limit`, `format=compact` default, and the
  rg/grep/cat steer in its description.
- `format`: `compact` = token-thrifty markdown for the agent to read; `json` = structured for tool chaining.
- Mutation (`edit`) and admin (`workspace`) are the only tools carrying an `operation`/enum.
- Output strings are markdown (codesearch's current style), but every tool also emits the telemetry record below.

## Telemetry & logging — day-1 requirement

The telemetry table that drove this design must exist in Miller from the first commit. Two layers:

**1. Structured logging (Serilog — already wired in commit `e208a0a`, daily rolling files).** Per-tool-call event
+ errors with context. Add a per-call correlation id; log tool, operation, truncated/hashed query, outcome, and
exceptions with full context. This is for debugging.

**2. Metrics ledger (the dashboard data).** A single tool-invocation interceptor (decorator around tool dispatch —
NOT per-tool code) captures, per call:

| field | purpose |
|---|---|
| `tool`, `operation`/`mode` | grouping |
| `started_at` (UTC), `duration_ms` | latency → avg / p50 / p95 / p99 (use a Histogram) |
| `outcome` (ok/error), `error_kind` | reliability |
| `workspace_id` | scoping |
| `result_count` | output shape |
| `bytes_examined` | work proxy (postings/rows scanned) |
| `bytes_returned` + `est_tokens_returned` | **the north-star KPI: token cost to the agent** |
| `index_fresh` (bool) | was the index stale at call time |

- **Storage:** append-only `tool_telemetry` SQLite table with retention pruning (eros has this ledger + budgets).
  Aggregations computed for the dashboard view — literally reproduce julie's tool-breakdown screen.
- **.NET idiom:** `System.Diagnostics.Metrics.Meter` (Counter + Histogram, gives p95 for free) feeding a SQLite
  sink the dashboard reads. AOT-friendly. Estimate returned tokens with `Microsoft.ML.Tokenizers` (the spike noted
  .NET 10 tokenizer perf) or a cheap chars/4 heuristic.
- **Soft budgets (phase-1.5):** per-tool latency + token budgets that log a WARN when exceeded — cheap early-warning
  for "this tool got slow or fat," which is exactly the tool-refinement use case. eros has hard gates; Miller starts warn-only.
- **VERIFY:** whether the MCP C# SDK exposes a tool-invocation filter/middleware hook for the central interceptor.
  If not, wrap each tool body in a one-line `using var _ = Telemetry.Measure(tool, op)` scope — still centralized,
  minimal boilerplate. (Don't guess the SDK's hook; confirm before building the interceptor.)

**Why day-1, not later:** the examined/returned columns are what expose token waste; latency is what reveals
adoption-killers (blast_radius). The product's north star is fast + token-thrifty, so this ledger is the feedback
loop that tells us whether we're hitting it. Retrofitting telemetry after the fact means flying blind through the
period when tool design is most malleable.

## Explicitly deferred (not MVP)
- Memory tool: stays SEPARATE (use goldfish). codesearch's `MemoryTool.cs` is marked for removal from Miller scope.
- Semantic search mode (lexical-first; semantic opt-in only if ever justified).
- Hard budget gates, the dashboard's agent-runner control plane, test-confidence + dependency/CVE intel (phase-2).
