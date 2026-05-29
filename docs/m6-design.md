# M6 — `edit`: index-aware, preview-first, freshness-gated

Implementation spec for M6. Every contract is **verified against the live pinned `julie-server` v7.12.2 /
schema 26** (probed the `symbols` + `identifiers` + `files` tables directly) and against the M2/M3 code M6
builds on (`SmartTargetResolver`, `ExtractReader`, `StalenessCheck`, `SingleWriterLock`, `IExtractOps`). The
tool surface is `docs/findings/miller-toolbox.md` §6 (the agreed shape). Grounded; no placeholders.

## Goal
Edit code with index awareness: **preview a diff by default, never write unless `apply=true`**, refuse a stale
target (with an `allow_stale` escape), and after writing converge the index. Operations: text replace, symbol
body/signature rewrite, workspace-wide rename, insert before/after, add doc. **Exit: preview diffs are correct;
apply writes atomically and triggers reindex; the stale gate blocks; rename updates all (name-matched) sites.**

## The seam
- **Miller.Core/Editing** gains the **pure** edit engine — byte-span splicing, per-operation planning, rename
  planning, unified-diff — unit-tested with plain strings, zero I/O.
- **Miller.Indexing** gains the span reads M6 needs (`ReadEditSpan`, `ReadIdentifierSites`, indexed file text).
- **Miller.Server** gains the `edit` tool (resolve → gate → plan → preview/apply), the atomic `EditApplier`
  (TOCTOU re-check + rollback), and the post-apply write-through.
- Default test suite stays **< 10s**; real-extract / real-file-write tests are `[Trait("Category","Scale")]`.

---

## Verified facts (live probe, pinned v7.12.2 / schema 26)
1. **`symbols` carries full byte spans:** `start_byte`/`end_byte` (the WHOLE symbol) AND
   `body_start_byte`/`body_end_byte` (the body), plus `start_line`/`end_line`, `doc_comment`, `signature`. E.g.
   a class `OrderService` → `start_byte=49, end_byte=123, body_start_byte=75, body_end_byte=123`; its method
   `Total` → `start_byte=81, body_start_byte=112`. So **signature span = `[start_byte, body_start_byte)`**, **body
   span = `[body_start_byte, body_end_byte)`**.
2. **`identifiers` carries exact per-occurrence byte spans:** a `Total` call → `start_byte=120, end_byte=125`
   (precisely the 5-char token). So rename replaces an exact byte range per site — no fuzzy whole-word matching.
   `identifiers` also has `start_line`/`start_col`, `containing_symbol_id`, and `target_symbol_id` (**always NULL**
   at extract — unresolved; the homonym caveat below).
3. **`files.content` holds the full file text** (verified: 124-byte file → `length(content)=124`), and `files.hash`
   is blake3. The freshness gate compares `files.content` (the indexed snapshot) against the current disk text.
4. **julie byte offsets are absolute UTF-8 byte indices** (`ExtractReader.SliceByBytes` already encodes→slices→
   decodes). All M6 splicing is UTF-8 byte-exact, not UTF-16 char indices.
5. **M3 primitives M6 consumes:** `StalenessCheck.Check(IndexedSnapshot, CurrentProbe)` (hash + exact-text, no
   mtime); `SingleWriterLock.TryAcquire(millerDir)` (the leader lease); `IExtractOps.Update(path)` (targeted
   reindex, canonical-path-safe). `ExtractReader` opens `Mode=ReadOnly` via the shared `SqliteReadOnlyAccess`.

---

## Decision log (resolved — do not re-litigate)
1. **Tool surface = `miller-toolbox.md` §6 exactly.** Params: `operation` (enum: replace_text,
   replace_symbol_body, replace_symbol_signature, rename_symbol, insert_before, insert_after, add_doc), `target`
   (smart-resolved), `old_text?`, `new_text?`, `occurrence` (first|last|all, default first), `dry_run` (default
   **true**), `apply` (default false), `allow_stale` (default false), `scope?` (the cross-tool §2 override:
   disambiguate an ambiguous symbol name to a file), `format` (compact|json). **No `preview_id`**
   — eros has it, but Miller's agreed surface does not; the dry_run→re-call-with-apply two-step IS the preview flow.
2. **Edits are byte-span splices on the CURRENT DISK content.** Spans come from julie's byte offsets (fact 1/2).
   The planner is pure (content + params + span → list of `TextEdit{StartByte,EndByte,Replacement}`); the splicer
   applies non-overlapping edits right-to-left, UTF-8 byte-exact. Planning/diff live in Core; file I/O is thin in
   Server. The edit operates on what's actually on disk now (not `files.content`, which is only the gate's snapshot).
3. **Freshness gate = indexed-text vs disk-text.** Read `files.content` (indexed snapshot) + the current disk
   text; SHA256 both and run `StalenessCheck` (same algorithm both sides — sidesteps julie's blake3, no new dep,
   content-authoritative not mtime). `Stale && !allow_stale` → refuse with an actionable message (`index stale for
   <file> — run workspace refresh, or pass allow_stale`). `allow_stale=true` → proceed but tag the result
   `stale_allowed`. The gate catches edits made to the file *since it was indexed*.
4. **Apply = writer-locked, TOCTOU-rechecked, atomic, all-or-nothing.** On `apply=true`: acquire the
   `SingleWriterLock` (or confirm leadership); for each target file RE-READ it and confirm it still equals the
   content the plan was computed against (else abort: `file changed before edit commit`); write each via a temp
   file + atomic replace; on ANY failure roll back every already-replaced file to its original and delete temps
   (eros's pattern). `allow_stale` relaxes "disk differs from INDEX" (gate, step 3) but NEVER this TOCTOU
   "disk changed under me mid-edit" check.
5. **`rename_symbol` = workspace-wide, exact-span, name-matched, preview-gated.** Replace the exact byte token at
   every `identifiers` occurrence of the name + the definition's name token, across all files. Because
   `target_symbol_id` is NULL (no resolution until M4), matching is **name-based** — a homonym (an unrelated
   symbol with the same name) is also matched. This is contained by preview-first: the dry_run preview lists
   EVERY site grouped by file with a clear "name-based match, N sites across M files — review before apply" note,
   and nothing is written until a deliberate `apply=true`. **Resolution-scoped rename (only the target symbol's
   refs) lands with M4** — this is the honest capability given today's unresolved extract, not a silent reduction.
6. **Write-through = converge after apply.** After a successful apply, if this instance holds the
   `SingleWriterLock` (it is the leader) call `IExtractOps.Update(file)` for each changed file (immediate,
   deterministic reindex + revision bump → this instance's `FreshnessService` swaps). If it is NOT the leader,
   the file write already emits a FileSystemWatcher event the leader's M3 watcher reconciles. Either path
   converges; the next edit's freshness gate is the backstop. (Multi-file rename triggers an Update per file.)
7. **Span math (byte-exact):** body → `[body_start_byte, body_end_byte)`; signature → `[start_byte,
   body_start_byte)`; insert_before → zero-width at `start_byte`; insert_after → zero-width at `end_byte`; add_doc
   → zero-width at the start of `start_line` (line→byte mapped on the disk content), inserting `new_text` + a
   newline, doc-comment-prefix preserved as given by the caller. Symbols with NULL body spans (e.g. a field) reject
   body/signature ops with a clear message.

---

## Components

### 1. Pure edit engine (Miller.Core/Editing — zero I/O)
- **`EditOperation`** enum + **`Occurrence`** {First, Last, All}.
- **`TextEdit(int StartByte, int EndByte, string Replacement)`** — one byte-span splice; `EndByte==StartByte` is
  a pure insertion. **`SymbolEditSpan(int StartByte, int EndByte, int? BodyStartByte, int? BodyEndByte, int
  StartLine, string Name)`** — the span info an op needs. **`PlannedEdit(string FilePath, string OldContent,
  string NewContent, IReadOnlyList<TextEdit> Edits)`** — the per-file result.
- **`TextSplicer.Apply(string content, IReadOnlyList<TextEdit> edits) → string`** — validates non-overlapping +
  in-range, applies right-to-left on UTF-8 bytes, decodes. Throws on overlap/out-of-range (caller turns into a
  clean error).
- **`EditPlanner`** (static, pure) — one method per single-file op returning `TextEdit`s or an `EditError`
  (e.g. `old_text not found`, `occurrence=all with 0 matches`, `body span unavailable`): `ReplaceText`
  (occurrence first/last/all over the content), `ReplaceSymbolBody`, `ReplaceSymbolSignature`, `InsertBefore`,
  `InsertAfter`, `AddDoc` (line→byte on the content).
- **`RenamePlanner`** (static, pure) — given `oldName`, `newName`, and per-file occurrence byte-spans + the
  def-site name-token span, produce a `PlannedEdit` per file (each occurrence → `TextEdit(span, newName)`).
  Validates `newName` is a plausible identifier; surfaces the full per-file site list for the preview.
- **`UnifiedDiff.Render(string oldContent, string newContent, string path) → string`** — minimal LCS line-diff
  with hunks; empty when unchanged. The preview renderer.

### 2. Read-layer extensions (Miller.Indexing)
- `ExtractReader.ReadEditSpan(dbPath, symbolId) → SymbolEditSpan?` — `SELECT start_byte, end_byte,
  body_start_byte, body_end_byte, start_line, name FROM symbols WHERE id=$id`.
- `ExtractReader.ReadIdentifierSites(dbPath, name) → IReadOnlyList<IdentifierSite>` where
  `IdentifierSite(string FilePath, int StartByte, int EndByte, int StartLine)` —
  `SELECT file_path, start_byte, end_byte, start_line FROM identifiers WHERE name=$name ORDER BY file_path, start_byte`.
- `ExtractReader.ReadIndexedFileText(dbPath, filePath) → string?` — promote the existing private
  `ReadFileContent` (the gate's indexed snapshot).

### 3. `edit` tool + apply (Miller.Server)
- **`FreshnessGate`** (Server helper): `Check(dbPath, file, diskText) → Fresh|Stale` by SHA256-ing
  `ReadIndexedFileText` vs `diskText` through `StalenessCheck`. Missing indexed content → treat as Stale (can't
  verify) unless `allow_stale`.
- **`EditApplier`** (Server infra): `Apply(IReadOnlyList<PlannedEdit>) ` under the writer lock — TOCTOU re-read +
  compare each file, temp-write, atomic `File.Move(overwrite)`, reverse-order rollback on failure, temp cleanup
  in `finally`. Pure planning already done; this is the I/O + transaction.
- **`EditTool`** `[McpServerToolType]`: (1) resolve `target` via `SmartTargetResolver` (text ops → file; symbol
  ops → symbol → id → `ReadEditSpan`; rename → `ReadIdentifierSites` + def span); (2) `FreshnessGate` (skip on
  `allow_stale`, tag the result); (3) read disk content, `EditPlanner`/`RenamePlanner` → `PlannedEdit`(s) +
  `UnifiedDiff`; (4) `dry_run` (default) → return the diff preview (+ rename site summary), no write; (5)
  `apply=true` → `EditApplier.Apply` → write-through (`IExtractOps.Update` per file if leader, else the watcher).
  Ambiguous target → candidates; symbol/file not found → clean message; planner error → clean message. Returns
  compact markdown (diff) or JSON. Telemetry: `op`, target hash, outcome, `index_fresh`.

---

## Test strategy
**Default suite (< 10s, no julie-server binary, no real apply to the repo):**
- `TextSplicerTests`: multi-edit non-overlapping apply (right-to-left), pure insert (empty span), UTF-8
  multibyte correctness (e.g. an emoji/accent before the span), overlap → throws, out-of-range → throws.
- `EditPlannerTests`: each op → correct `TextEdit`(s) over a known fixture string; replace_text first/last/all +
  not-found error; signature vs body span math; insert_before/after positions; add_doc line→byte + newline;
  body op on a NULL-body symbol → error.
- `RenamePlannerTests`: multi-site/multi-file plan from occurrence spans; def-site token located; invalid
  new_name → error; the preview site list (count + per-file grouping); a homonym site is INCLUDED (pins the
  documented name-based behavior) and is visible in the preview.
- `UnifiedDiffTests`: add/remove/change lines → correct hunks; identical → empty.
- `ExtractReaderEditTests` (synthesized fixture): `ReadEditSpan` (spans + NULL body), `ReadIdentifierSites`
  (multiple, ordered, empty), `ReadIndexedFileText`.
- `FreshnessGateTests`: indexed==disk → Fresh; differ → Stale; `allow_stale` overrides; missing indexed content
  → Stale-unless-allow_stale.
- `EditApplierTests` (temp files in the test's own tmp dir): atomic single-file write; TOCTOU (mutate the file
  between plan and apply → abort, original intact); multi-file rollback (2nd write fails → 1st restored); temps
  cleaned.
- `EditToolTests` (synthesized fixture + temp files): dry_run returns a diff and writes NOTHING; apply writes +
  invokes write-through (recorded via a fake `IExtractOps`); stale target blocks (and `allow_stale` proceeds);
  ambiguous target → candidates; not-found → message; each operation end-to-end.

**Scale suite (`[Trait("Category","Scale")]`):**
- `LiveEditTests`: restore julie-server → scan a temp repo → `edit` body-replace + add-doc + a cross-file rename
  with `apply=true` → the files on disk are correct → write-through reindexes (revision bumps) → `inspect`/`search`
  reflect the change → an externally-modified file trips the freshness gate (refused without `allow_stale`).

Banned-test discipline (CLAUDE.md): assert on produced content/diffs, cover not-found / overlap / TOCTOU /
rollback / stale / homonym / NULL-span paths, parameterize, no smoke-only tests, keep the existing 402 green,
default suite < 10s.

## Implementation order (strict TDD)
1. `TextSplicer` (Core) → red→green.
2. `EditPlanner` single-file operations (Core) → red→green.
3. `UnifiedDiff` (Core) → red→green.
4. `RenamePlanner` (Core) → red→green.
5. `ExtractReader.ReadEditSpan` + `ReadIdentifierSites` + `ReadIndexedFileText` (Indexing) → red→green.
6. `FreshnessGate` (Server) → red→green.
7. `EditApplier` (atomic write + TOCTOU + rollback) → red→green.
8. `EditTool` (resolve → gate → plan → dry_run/apply → write-through) → red→green.
9. DI wiring + tool registration + the write-through seam (leader → `IExtractOps.Update`, else watcher).
10. `LiveEditTests` (Scale).

**Verify:** `dotnet build Miller.slnx -c Debug` → 0/0; `dotnet test --filter "Category!=Scale"` → all green
(existing 402 + new), < 10s. Then the live Scale path.

**Exit:** `edit` previews a correct diff by default and writes nothing; `apply=true` writes atomically (with
TOCTOU + rollback) and converges the index; a stale target is blocked unless `allow_stale`; workspace-wide
rename updates every name-matched site, preview-gated. The 6-tool read/write surface is complete bar M4's `trace`.
