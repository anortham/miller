# Task 4 — `inspect depth=overview` strips doc-comment lines from body preview

## What I implemented
- `BodyPreview` (`src/Miller.Server/Tools/InspectTool.cs`) now filters doc-comment lines out of the
  normalized body **before** applying the `OverviewBodyPreviewMaxLines=16` / `OverviewBodyPreviewMaxChars=700`
  caps, so the overview budget shows real code instead of member docs that already duplicate the `doc:` section.
- Added a private helper `FilterDocCommentLines(IReadOnlyList<string>)` next to `BodyPreview`. It drops:
  - lines whose trimmed form starts with `///` or `//!` (C#/Rust doc comments),
  - `/** … */` blocks inclusive (a small `inBlockDoc` flag: a line starting `/**` opens the block unless it
    also contains `*/` on the same line; the block ends on the first line containing `*/`).
  It keeps ordinary `//` and `#` comments (code commentary, not doc duplication). Python `"""` docstrings are
  string literals and are left untouched (not in scope).
- Truncation semantics: caps are applied to the **filtered** lines exactly as before. Dropping doc lines alone
  never sets `Truncated`; `Truncated` is only set when the filtered content still exceeds the line or char cap.
- `depth=full` path is untouched — it renders `body.Text` directly and never calls `BodyPreview`, so full output
  is byte-identical to before.

Edit was confined strictly to the `BodyPreview` region plus the new adjacent private helper; the
`RenderSymbolCompact` references/callees blocks were not touched (clean composition with the sibling worker).

## Miller calls used + what each confirmed
- `inspect BodyPreview depth=overview` — confirmed the method signature, its call sites
  (`RenderSymbolCompact` :375 compact path, `RenderSymbolJson` :462 JSON path) and the exact pre-edit body,
  so I knew both overview render paths share `BodyPreview` and the JSON `body_preview` field benefits too.
- ToolSearch loaded the Miller MCP tool schemas (server serves the main checkout, content-identical at HEAD).

## API-shape evidence
- Caps `OverviewBodyPreviewMaxLines=16` / `OverviewBodyPreviewMaxChars=700` at InspectTool.cs:118-119.
- `BodyPreviewResult(string? Text, bool Truncated)` record struct at :574; consumed in compact
  (:447-453, appends `"... body preview truncated (use depth=full)"` only when `Truncated`) and JSON
  (:538-551, writes `body_preview` / `body_preview_truncated`).
- Body text source: `ExtractReader.ReadBody` → `SourceTextDecoder.SliceUtf8ByteSpan(text, startByte, endByte)`
  where `endByte` is **exclusive** and clamped to byte length (SourceTextDecoder.cs:42-56) — so the test fixture
  uses `BodyEndByte = Encoding.UTF8.GetByteCount(content)` to slice the whole file.

## Verification
- Invariant proven: the overview body preview drops `///`, `//!`, and `/** … */` doc-comment lines while
  keeping plain `//` and `#` comments and code; filtering alone does not flag truncation; `depth=full` body
  is unchanged and still contains every doc-comment line.
- Scope: `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~InspectToolTests"` (worker-red-green).
- Result: **Passed! Failed: 0, Passed: 39, Skipped: 0** (2 new tests), Duration ~0.5s.
- Timestamp: 2026-07-02.

## Files changed
- `src/Miller.Server/Tools/InspectTool.cs` — `BodyPreview` + new `FilterDocCommentLines` helper.
- `tests/Miller.Tests/Server/InspectToolTests.cs` — `DocCommentBodyContent`/`DocCommentBodyFixture` +
  `Run_SymbolOverview_BodyPreview_DropsDocCommentLines_KeepsCodeAndPlainComments` and
  `Run_SymbolFull_Body_IsByteIdenticalIncludingDocComments`.

## Judgment calls
- Truncation: implemented so caps apply to filtered lines and dropping alone never sets `Truncated`, matching
  the spec ("dropping alone doesn't mean truncation"). A body short only after filtering shows no truncation
  note; a genuinely long body still does.
- Single-line `/** … */` is dropped without opening the block flag (checks for `*/` on the same line).
- Kept `#` and `//` deliberately — they are commentary, not the doc duplication the audit flagged.

## Self-review findings
- Verified `depth=full` never routes through `BodyPreview` (separate branch at InspectTool.cs:454-457), so
  the byte-identical guarantee holds structurally, not just by test.
- JSON overview path also benefits (same helper) — no separate change needed; existing JSON body-preview tests
  still pass.

## Concerns
- None. Edit is localized; other workers' InspectTool regions were not touched.
