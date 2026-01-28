# Phase 7: Symbol Relationships Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Persist symbol relationships and enable caller/callee queries for find-callers and explain-symbol features.

**Architecture:** Add relationships table to LanceDB, expose query APIs through FFI, create C# service methods, and build MCP tools. Relationships are already extracted by the Rust extractors - this phase persists and queries them.

**Tech Stack:** Rust (LanceDB, arrow), UniFFI bindings, C# MCP tools

---

## Prerequisites

Phase 6 complete with:
- Working MCP server with search, index, memory tools
- 29 passing tests
- Claude Code plugin with commands and skills

## Key Insight

The Rust extractors already extract relationships (calls, implements, uses, extends, etc.) with confidence scores. They're just not persisted - the `ExtractionResults` struct contains relationships but only symbols get written to LanceDB.

---

### Task 1: Add Relationship Schema to LanceDB

**Files:**
- Modify: `rust/codesearch-core/src/schema.rs`

**Step 1: Add relationship schema**

Add to `rust/codesearch-core/src/schema.rs` after the symbol schema:

```rust
/// Schema for the relationships table
pub fn relationship_schema() -> Schema {
    Schema::new(vec![
        Field::new("from_symbol_id", DataType::Utf8, false),
        Field::new("to_symbol_id", DataType::Utf8, false),
        Field::new("kind", DataType::Utf8, false),
        Field::new("file_path", DataType::Utf8, false),
        Field::new("line_number", DataType::UInt32, false),
        Field::new("confidence", DataType::Float32, false),
    ])
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 3: Commit**

```bash
git add rust/codesearch-core/src/schema.rs
git commit -m "feat(core): add relationship schema to LanceDB"
```

---

### Task 2: Add Relationship Storage to Engine

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add relationship table creation**

In the `SearchEngine::new()` method, after creating the symbols table, add relationships table creation:

```rust
// Create relationships table if it doesn't exist
if !table_names.contains(&"relationships".to_string()) {
    let empty_batch = RecordBatch::try_new(
        Arc::new(schema::relationship_schema()),
        vec![
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(UInt32Array::from(Vec::<u32>::new())),
            Arc::new(Float32Array::from(Vec::<f32>::new())),
        ],
    )?;
    db.create_table("relationships", empty_batch)?;
}
```

**Step 2: Add relationship input struct**

Add near the top of the file:

```rust
/// Input for adding a relationship
pub struct RelationshipInput {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}
```

**Step 3: Add add_relationships method**

Add to the `SearchEngine` impl:

```rust
/// Add relationships to the database
pub fn add_relationships(&self, relationships: Vec<RelationshipInput>) -> Result<usize> {
    if relationships.is_empty() {
        return Ok(0);
    }

    let from_ids: Vec<&str> = relationships.iter().map(|r| r.from_symbol_id.as_str()).collect();
    let to_ids: Vec<&str> = relationships.iter().map(|r| r.to_symbol_id.as_str()).collect();
    let kinds: Vec<&str> = relationships.iter().map(|r| r.kind.as_str()).collect();
    let paths: Vec<&str> = relationships.iter().map(|r| r.file_path.as_str()).collect();
    let lines: Vec<u32> = relationships.iter().map(|r| r.line_number).collect();
    let confidences: Vec<f32> = relationships.iter().map(|r| r.confidence).collect();

    let batch = RecordBatch::try_new(
        Arc::new(schema::relationship_schema()),
        vec![
            Arc::new(StringArray::from(from_ids)),
            Arc::new(StringArray::from(to_ids)),
            Arc::new(StringArray::from(kinds)),
            Arc::new(StringArray::from(paths)),
            Arc::new(UInt32Array::from(lines)),
            Arc::new(Float32Array::from(confidences)),
        ],
    )?;

    let table = self.db.open_table("relationships")?;
    table.add(vec![batch])?;

    Ok(relationships.len())
}
```

**Step 4: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 5: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add relationship storage to engine"
```

---

### Task 3: Add Relationship Query Methods

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add get_callers method**

Add to the `SearchEngine` impl:

```rust
/// Get symbols that call the given symbol
pub fn get_callers(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
    let table = self.db.open_table("relationships")?;

    // Filter by to_symbol_id (who calls this symbol)
    let results = table
        .query()
        .filter(format!("to_symbol_id = '{}'", symbol_id.replace("'", "''")))
        .filter("kind = 'Calls'")
        .limit(limit)
        .execute()?;

    self.collect_relationships(results)
}

/// Get symbols that the given symbol calls
pub fn get_callees(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
    let table = self.db.open_table("relationships")?;

    // Filter by from_symbol_id (who this symbol calls)
    let results = table
        .query()
        .filter(format!("from_symbol_id = '{}'", symbol_id.replace("'", "''")))
        .filter("kind = 'Calls'")
        .limit(limit)
        .execute()?;

    self.collect_relationships(results)
}

/// Get all relationships for a symbol (both directions)
pub fn get_relationships(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
    let table = self.db.open_table("relationships")?;

    let results = table
        .query()
        .filter(format!(
            "from_symbol_id = '{}' OR to_symbol_id = '{}'",
            symbol_id.replace("'", "''"),
            symbol_id.replace("'", "''")
        ))
        .limit(limit)
        .execute()?;

    self.collect_relationships(results)
}
```

**Step 2: Add RelationshipResult and helper**

Add near the other result types:

```rust
/// Result of a relationship query
pub struct RelationshipResult {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

impl SearchEngine {
    fn collect_relationships(&self, results: impl RecordBatchIterator) -> Result<Vec<RelationshipResult>> {
        let mut relationships = Vec::new();

        for batch_result in results {
            let batch = batch_result?;

            let from_ids = batch.column_by_name("from_symbol_id")
                .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
            let to_ids = batch.column_by_name("to_symbol_id")
                .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
            let kinds = batch.column_by_name("kind")
                .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
            let paths = batch.column_by_name("file_path")
                .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
            let lines = batch.column_by_name("line_number")
                .unwrap().as_any().downcast_ref::<UInt32Array>().unwrap();
            let confidences = batch.column_by_name("confidence")
                .unwrap().as_any().downcast_ref::<Float32Array>().unwrap();

            for i in 0..batch.num_rows() {
                relationships.push(RelationshipResult {
                    from_symbol_id: from_ids.value(i).to_string(),
                    to_symbol_id: to_ids.value(i).to_string(),
                    kind: kinds.value(i).to_string(),
                    file_path: paths.value(i).to_string(),
                    line_number: lines.value(i),
                    confidence: confidences.value(i),
                });
            }
        }

        Ok(relationships)
    }
}
```

**Step 3: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 4: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add relationship query methods"
```

---

### Task 4: Expose Relationships in FFI

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add FFI types for relationships**

Add the FFI-safe types:

```rust
/// FFI-safe relationship input
#[derive(uniffi::Record)]
pub struct RelationshipInput {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

/// FFI-safe relationship result
#[derive(uniffi::Record)]
pub struct RelationshipResult {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}
```

**Step 2: Add FFI methods to CodeSearchEngine**

Add to the `CodeSearchEngine` impl:

```rust
/// Add relationships to the database
pub fn add_relationships(&self, relationships: Vec<RelationshipInput>) -> Result<u64, CodeSearchError> {
    let inputs: Vec<codesearch_core::engine::RelationshipInput> = relationships
        .into_iter()
        .map(|r| codesearch_core::engine::RelationshipInput {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        })
        .collect();

    self.engine
        .add_relationships(inputs)
        .map(|n| n as u64)
        .map_err(|e| CodeSearchError::DatabaseError(e.to_string()))
}

/// Get symbols that call the given symbol
pub fn get_callers(&self, symbol_id: String, limit: u32) -> Result<Vec<RelationshipResult>, CodeSearchError> {
    self.engine
        .get_callers(&symbol_id, limit as usize)
        .map(|results| results.into_iter().map(|r| RelationshipResult {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }).collect())
        .map_err(|e| CodeSearchError::DatabaseError(e.to_string()))
}

/// Get symbols that the given symbol calls
pub fn get_callees(&self, symbol_id: String, limit: u32) -> Result<Vec<RelationshipResult>, CodeSearchError> {
    self.engine
        .get_callees(&symbol_id, limit as usize)
        .map(|results| results.into_iter().map(|r| RelationshipResult {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }).collect())
        .map_err(|e| CodeSearchError::DatabaseError(e.to_string()))
}

/// Get all relationships for a symbol
pub fn get_relationships(&self, symbol_id: String, limit: u32) -> Result<Vec<RelationshipResult>, CodeSearchError> {
    self.engine
        .get_relationships(&symbol_id, limit as usize)
        .map(|results| results.into_iter().map(|r| RelationshipResult {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }).collect())
        .map_err(|e| CodeSearchError::DatabaseError(e.to_string()))
}
```

**Step 3: Rebuild UniFFI bindings**

Run: `cd rust && cargo build --release`

**Step 4: Regenerate C# bindings**

Run the UniFFI bindgen command (or however bindings are regenerated in this project).

**Step 5: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): expose relationship methods in UniFFI"
```

---

### Task 5: Add Relationship Methods to SearchService

**Files:**
- Modify: `src/Codesearch.Server/Services/SearchService.cs`

**Step 1: Add relationship methods**

Add to the `SearchService` class:

```csharp
/// <summary>
/// Add relationships to the database.
/// </summary>
public ulong AddRelationships(List<uniffi.codesearch_ffi.RelationshipInput> relationships)
{
    return _engine.AddRelationships(relationships);
}

/// <summary>
/// Get symbols that call the given symbol.
/// </summary>
public List<uniffi.codesearch_ffi.RelationshipResult> GetCallers(string symbolId, uint limit = 50)
{
    return _engine.GetCallers(symbolId, limit);
}

/// <summary>
/// Get symbols that the given symbol calls.
/// </summary>
public List<uniffi.codesearch_ffi.RelationshipResult> GetCallees(string symbolId, uint limit = 50)
{
    return _engine.GetCallees(symbolId, limit);
}

/// <summary>
/// Get all relationships for a symbol.
/// </summary>
public List<uniffi.codesearch_ffi.RelationshipResult> GetRelationships(string symbolId, uint limit = 100)
{
    return _engine.GetRelationships(symbolId, limit);
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): add relationship methods to SearchService"
```

---

### Task 6: Update IndexService to Store Relationships

**Files:**
- Modify: `src/Codesearch.Server/Services/IndexService.cs`

**Step 1: Update indexing to include relationships**

The IndexService needs to extract and store relationships during indexing. Update the indexing logic to:

1. Call the extractor to get both symbols AND relationships
2. Store relationships via `SearchService.AddRelationships()`

This requires understanding how the current IndexService works with the extractors. The extraction already happens - we just need to capture and persist the relationships.

Add relationship storage after symbol storage in the indexing flow:

```csharp
// After adding symbols, add relationships
var relationshipInputs = extractionResult.Relationships
    .Where(r => !string.IsNullOrEmpty(r.ToSymbolId)) // Only resolved relationships
    .Select(r => new uniffi.codesearch_ffi.RelationshipInput(
        fromSymbolId: r.FromSymbolId,
        toSymbolId: r.ToSymbolId,
        kind: r.Kind.ToString(),
        filePath: r.FilePath,
        lineNumber: (uint)r.LineNumber,
        confidence: r.Confidence
    ))
    .ToList();

if (relationshipInputs.Count > 0)
{
    _searchService.AddRelationships(relationshipInputs);
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/IndexService.cs
git commit -m "feat(server): store relationships during indexing"
```

---

### Task 7: Create RelationshipTool

**Files:**
- Create: `src/Codesearch.Server/Tools/RelationshipTool.cs`

**Step 1: Create the relationship tool**

Create `src/Codesearch.Server/Tools/RelationshipTool.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class RelationshipTool
{
    [McpServerTool]
    [Description("Find callers, callees, and relationships for symbols. Operations: callers, callees, explain.")]
    internal static string Relationships(
        SearchService searchService,
        [Description("Operation: callers, callees, or explain")] string operation,
        [Description("Symbol ID or name to look up")] string symbol,
        [Description("Maximum results")] int limit = 20)
    {
        // First, find the symbol if given a name instead of ID
        var symbolId = symbol;
        if (!symbol.Contains("::") && !symbol.Contains("/"))
        {
            // Looks like a name, search for it
            var searchResults = searchService.SearchText(symbol, 1);
            if (searchResults.Count == 0)
            {
                return $"No symbol found matching '{symbol}'.";
            }
            symbolId = searchResults[0].id;
        }

        return operation.ToLowerInvariant() switch
        {
            "callers" => GetCallers(searchService, symbolId, limit),
            "callees" => GetCallees(searchService, symbolId, limit),
            "explain" => ExplainSymbol(searchService, symbolId, limit),
            _ => $"Unknown operation: {operation}. Use: callers, callees, or explain."
        };
    }

    private static string GetCallers(SearchService searchService, string symbolId, int limit)
    {
        var callers = searchService.GetCallers(symbolId, (uint)limit);

        if (callers.Count == 0)
        {
            return "No callers found for this symbol.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Callers ({callers.Count})");
        sb.AppendLine();

        foreach (var caller in callers)
        {
            sb.AppendLine($"- `{caller.from_symbol_id}` at `{caller.file_path}:{caller.line_number}`");
            sb.AppendLine($"  Confidence: {caller.confidence:P0}");
        }

        return sb.ToString();
    }

    private static string GetCallees(SearchService searchService, string symbolId, int limit)
    {
        var callees = searchService.GetCallees(symbolId, (uint)limit);

        if (callees.Count == 0)
        {
            return "No callees found for this symbol.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Callees ({callees.Count})");
        sb.AppendLine();

        foreach (var callee in callees)
        {
            sb.AppendLine($"- `{callee.to_symbol_id}` at `{callee.file_path}:{callee.line_number}`");
            sb.AppendLine($"  Confidence: {callee.confidence:P0}");
        }

        return sb.ToString();
    }

    private static string ExplainSymbol(SearchService searchService, string symbolId, int limit)
    {
        // Get the symbol details
        var searchResults = searchService.SearchText(symbolId, 1);

        var sb = new StringBuilder();
        sb.AppendLine("## Symbol Details");
        sb.AppendLine();

        if (searchResults.Count > 0)
        {
            var sym = searchResults[0];
            sb.AppendLine($"- **Name**: `{sym.name}`");
            sb.AppendLine($"- **Kind**: {sym.kind}");
            sb.AppendLine($"- **Language**: {sym.language}");
            sb.AppendLine($"- **File**: `{sym.file_path}:{sym.start_line}-{sym.end_line}`");
            if (!string.IsNullOrEmpty(sym.signature))
            {
                sb.AppendLine($"- **Signature**: `{sym.signature}`");
            }
            if (!string.IsNullOrEmpty(sym.doc_comment))
            {
                sb.AppendLine();
                sb.AppendLine("### Documentation");
                sb.AppendLine(sym.doc_comment);
            }
        }
        else
        {
            sb.AppendLine($"- **ID**: `{symbolId}`");
        }

        sb.AppendLine();

        // Get callers
        var callers = searchService.GetCallers(symbolId, (uint)limit);
        sb.AppendLine($"### Callers ({callers.Count})");
        sb.AppendLine();
        if (callers.Count == 0)
        {
            sb.AppendLine("_No callers found._");
        }
        else
        {
            foreach (var caller in callers.Take(10))
            {
                sb.AppendLine($"- `{caller.from_symbol_id}` ({caller.file_path}:{caller.line_number})");
            }
            if (callers.Count > 10)
            {
                sb.AppendLine($"_...and {callers.Count - 10} more_");
            }
        }

        sb.AppendLine();

        // Get callees
        var callees = searchService.GetCallees(symbolId, (uint)limit);
        sb.AppendLine($"### Callees ({callees.Count})");
        sb.AppendLine();
        if (callees.Count == 0)
        {
            sb.AppendLine("_No callees found._");
        }
        else
        {
            foreach (var callee in callees.Take(10))
            {
                sb.AppendLine($"- `{callee.to_symbol_id}` ({callee.file_path}:{callee.line_number})");
            }
            if (callees.Count > 10)
            {
                sb.AppendLine($"_...and {callees.Count - 10} more_");
            }
        }

        return sb.ToString();
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Tools/RelationshipTool.cs
git commit -m "feat(server): add RelationshipTool for callers/callees/explain"
```

---

### Task 8: Add Relationship Tests

**Files:**
- Create: `tests/Codesearch.Tests/RelationshipTests.cs`

**Step 1: Create relationship tests**

Create `tests/Codesearch.Tests/RelationshipTests.cs`:

```csharp
using Xunit;
using Codesearch.Server.Services;

namespace Codesearch.Tests;

public class RelationshipTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;

    public RelationshipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_rel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.lance");
        _searchService = new SearchService(dbPath);
    }

    public void Dispose()
    {
        _searchService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddRelationships_StoresRelationships()
    {
        // Add test symbols first
        var symbol1 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::caller",
            name: "caller",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn caller()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var symbol2 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::callee",
            name: "callee",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn callee()",
            docComment: null,
            startLine: 10,
            endLine: 15,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol1, symbol2 },
            new List<List<float>> { vector, vector }
        );

        // Add relationship
        var relationship = new uniffi.codesearch_ffi.RelationshipInput(
            fromSymbolId: "func::caller",
            toSymbolId: "func::callee",
            kind: "Calls",
            filePath: "test.rs",
            lineNumber: 3,
            confidence: 0.95f
        );
        var count = _searchService.AddRelationships(
            new List<uniffi.codesearch_ffi.RelationshipInput> { relationship }
        );

        Assert.Equal(1UL, count);
    }

    [Fact]
    public void GetCallers_ReturnsCallers()
    {
        // Setup symbols and relationship
        var symbol1 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::caller",
            name: "caller",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn caller()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var symbol2 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::callee",
            name: "callee",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn callee()",
            docComment: null,
            startLine: 10,
            endLine: 15,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol1, symbol2 },
            new List<List<float>> { vector, vector }
        );
        _searchService.AddRelationships(new List<uniffi.codesearch_ffi.RelationshipInput> {
            new uniffi.codesearch_ffi.RelationshipInput(
                fromSymbolId: "func::caller",
                toSymbolId: "func::callee",
                kind: "Calls",
                filePath: "test.rs",
                lineNumber: 3,
                confidence: 0.95f
            )
        });

        // Query callers
        var callers = _searchService.GetCallers("func::callee", 10);

        Assert.Single(callers);
        Assert.Equal("func::caller", callers[0].from_symbol_id);
    }

    [Fact]
    public void GetCallees_ReturnsCallees()
    {
        // Setup (same as above)
        var symbol1 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::caller",
            name: "caller",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn caller()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var symbol2 = new uniffi.codesearch_ffi.SymbolInput(
            id: "func::callee",
            name: "callee",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn callee()",
            docComment: null,
            startLine: 10,
            endLine: 15,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol1, symbol2 },
            new List<List<float>> { vector, vector }
        );
        _searchService.AddRelationships(new List<uniffi.codesearch_ffi.RelationshipInput> {
            new uniffi.codesearch_ffi.RelationshipInput(
                fromSymbolId: "func::caller",
                toSymbolId: "func::callee",
                kind: "Calls",
                filePath: "test.rs",
                lineNumber: 3,
                confidence: 0.95f
            )
        });

        // Query callees
        var callees = _searchService.GetCallees("func::caller", 10);

        Assert.Single(callees);
        Assert.Equal("func::callee", callees[0].to_symbol_id);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/RelationshipTests.cs
git commit -m "test: add relationship query tests"
```

---

### Task 9: Update Plugin Documentation

**Files:**
- Modify: `.claude-plugin/README.md`

**Step 1: Add relationships tool documentation**

Add to the MCP Tools section in `.claude-plugin/README.md`:

```markdown
### relationships

Find callers, callees, and explain symbols.

```
relationships(operation="callers", symbol="functionName", limit=20)
relationships(operation="callees", symbol="functionName", limit=20)
relationships(operation="explain", symbol="functionName")
```

Operations:
- **callers**: Find all symbols that call the given symbol
- **callees**: Find all symbols that the given symbol calls
- **explain**: Get full context including definition, callers, and callees
```

**Step 2: Update settings template**

Add `mcp__codesearch__relationships` to `.claude/settings.local.json.example`.

**Step 3: Commit**

```bash
git add .claude-plugin/README.md .claude/settings.local.json.example
git commit -m "docs(plugin): add relationships tool documentation"
```

---

### Task 10: Final Verification

**Step 1: Build Rust components**

Run: `cd rust && cargo build --release`
Expected: Success

**Step 2: Regenerate bindings if needed**

Run the binding generation step.

**Step 3: Build C# components**

Run: `dotnet build`
Expected: Success

**Step 4: Run all tests**

Run: `dotnet test`
Expected: All tests pass (29 + 3 = 32 tests)

**Step 5: Final commit**

```bash
git add -A
git commit -m "feat(relationships): complete Phase 7 relationship infrastructure"
```

---

## Phase 7 Complete

At this point you have:
- Relationship schema in LanceDB
- Relationship storage during indexing
- Query APIs: get_callers, get_callees, get_relationships
- RelationshipTool with callers, callees, explain operations
- Tests for relationship queries
- Updated plugin documentation

**Next Phase (8):** Impact analysis and graph traversal.
