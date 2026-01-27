# Phase 1: Foundation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Set up the project scaffolding with .NET solution, Rust workspace, UniFFI interop, and basic LanceDB integration.

**Architecture:** .NET 10 host orchestrates a Rust engine via UniFFI. The Rust side handles tree-sitter extraction and LanceDB storage. This phase establishes the build pipeline and proves the interop works end-to-end.

**Tech Stack:** .NET 10, Rust (edition 2021), UniFFI, LanceDB, tree-sitter

---

## Prerequisites

- .NET 10 SDK installed (verified: 10.0.102)
- Rust toolchain with cargo
- `uniffi-bindgen-cs` tool for C# bindings

---

## Task 1: Initialize Rust Workspace

**Files:**
- Create: `rust/Cargo.toml` (workspace root)
- Create: `rust/codesearch-core/Cargo.toml`
- Create: `rust/codesearch-core/src/lib.rs`
- Create: `rust/codesearch-ffi/Cargo.toml`
- Create: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Create workspace Cargo.toml**

```bash
mkdir -p rust/codesearch-core/src rust/codesearch-ffi/src
```

Create `rust/Cargo.toml`:

```toml
[workspace]
members = ["codesearch-core", "codesearch-ffi"]
resolver = "2"

[workspace.package]
edition = "2021"
version = "0.1.0"
authors = ["murphy"]
license = "MIT"

[workspace.dependencies]
# Core
tokio = { version = "1", features = ["full"] }
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"
tracing = "0.1"

# Search
lancedb = "0.15"
tantivy = "0.22"

# Parsing - will add tree-sitter deps when we copy extractors

# FFI
uniffi = "0.28"

# Utilities
blake3 = "1.5"
thiserror = "2.0"

[profile.release]
opt-level = 3
lto = true
codegen-units = 1
strip = true
```

**Step 2: Create codesearch-core crate**

Create `rust/codesearch-core/Cargo.toml`:

```toml
[package]
name = "codesearch-core"
edition.workspace = true
version.workspace = true
authors.workspace = true
license.workspace = true

[dependencies]
tokio.workspace = true
serde.workspace = true
serde_json.workspace = true
tracing.workspace = true
lancedb.workspace = true
blake3.workspace = true
thiserror.workspace = true

# LanceDB requires arrow
arrow = { version = "53", default-features = false }

[dev-dependencies]
tempfile = "3"
```

Create `rust/codesearch-core/src/lib.rs`:

```rust
//! Codesearch core library - search engine powered by LanceDB

pub mod engine;
pub mod error;

pub use engine::CodeEngine;
pub use error::Error;

pub type Result<T> = std::result::Result<T, Error>;
```

Create `rust/codesearch-core/src/error.rs`:

```rust
use thiserror::Error;

#[derive(Error, Debug)]
pub enum Error {
    #[error("LanceDB error: {0}")]
    LanceDb(String),

    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
}

impl From<lancedb::Error> for Error {
    fn from(e: lancedb::Error) -> Self {
        Error::LanceDb(e.to_string())
    }
}
```

Create `rust/codesearch-core/src/engine.rs`:

```rust
//! Core search engine wrapping LanceDB

use crate::{Error, Result};
use std::path::Path;
use std::sync::Arc;

/// The main search engine struct
pub struct CodeEngine {
    db: Arc<lancedb::Connection>,
    db_path: String,
}

impl CodeEngine {
    /// Create a new CodeEngine with the given database path
    pub async fn new(db_path: &str) -> Result<Self> {
        // Ensure parent directory exists
        if db_path != ":memory:" {
            if let Some(parent) = Path::new(db_path).parent() {
                std::fs::create_dir_all(parent)?;
            }
        }

        let db = lancedb::connect(db_path).execute().await?;

        Ok(Self {
            db: Arc::new(db),
            db_path: db_path.to_string(),
        })
    }

    /// Get the database path
    pub fn db_path(&self) -> &str {
        &self.db_path
    }

    /// Check if the engine is healthy
    pub async fn health_check(&self) -> Result<bool> {
        // List tables to verify connection
        let _tables = self.db.table_names().execute().await?;
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    #[tokio::test]
    async fn test_engine_creation() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();
        assert!(engine.health_check().await.unwrap());
    }
}
```

**Step 3: Create codesearch-ffi crate**

Create `rust/codesearch-ffi/Cargo.toml`:

```toml
[package]
name = "codesearch-ffi"
edition.workspace = true
version.workspace = true
authors.workspace = true
license.workspace = true

[lib]
crate-type = ["cdylib", "staticlib"]
name = "codesearch_ffi"

[dependencies]
codesearch-core = { path = "../codesearch-core" }
uniffi.workspace = true
tokio.workspace = true

[build-dependencies]
uniffi = { workspace = true, features = ["build"] }
```

Create `rust/codesearch-ffi/src/lib.rs`:

```rust
//! UniFFI bindings for codesearch-core

use std::sync::Arc;
use tokio::runtime::Runtime;

uniffi::setup_scaffolding!();

/// FFI-safe wrapper around CodeEngine
#[derive(uniffi::Object)]
pub struct CodeSearchEngine {
    inner: codesearch_core::CodeEngine,
    runtime: Arc<Runtime>,
}

#[uniffi::export]
impl CodeSearchEngine {
    /// Create a new CodeSearchEngine
    #[uniffi::constructor]
    pub fn new(db_path: String) -> Result<Arc<Self>, CodeSearchError> {
        let runtime = Arc::new(
            Runtime::new().map_err(|e| CodeSearchError::Runtime(e.to_string()))?
        );

        let inner = runtime.block_on(async {
            codesearch_core::CodeEngine::new(&db_path).await
        })?;

        Ok(Arc::new(Self { inner, runtime }))
    }

    /// Get the database path
    pub fn db_path(&self) -> String {
        self.inner.db_path().to_string()
    }

    /// Check if the engine is healthy
    pub fn health_check(&self) -> Result<bool, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner.health_check().await.map_err(CodeSearchError::from)
        })
    }
}

/// FFI-safe error type
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum CodeSearchError {
    #[error("Database error: {0}")]
    Database(String),

    #[error("Runtime error: {0}")]
    Runtime(String),
}

impl From<codesearch_core::Error> for CodeSearchError {
    fn from(e: codesearch_core::Error) -> Self {
        CodeSearchError::Database(e.to_string())
    }
}
```

Create `rust/codesearch-ffi/build.rs`:

```rust
fn main() {
    uniffi::generate_scaffolding("src/codesearch.udl").unwrap();
}
```

Create `rust/codesearch-ffi/src/codesearch.udl`:

```
namespace codesearch {};

[Error]
enum CodeSearchError {
    "Database",
    "Runtime",
};

interface CodeSearchEngine {
    [Throws=CodeSearchError]
    constructor(string db_path);

    string db_path();

    [Throws=CodeSearchError]
    boolean health_check();
};
```

**Step 4: Verify Rust builds**

```bash
cd rust && cargo build
```

Expected: Successful build with no errors.

**Step 5: Run Rust tests**

```bash
cd rust && cargo test
```

Expected: `test_engine_creation` passes.

**Step 6: Commit**

```bash
git add rust/
git commit -m "feat: initialize Rust workspace with codesearch-core and codesearch-ffi"
```

---

## Task 2: Initialize .NET Solution

**Files:**
- Create: `codesearch.sln`
- Create: `src/Codesearch.Server/Codesearch.Server.csproj`
- Create: `src/Codesearch.Server/Program.cs`
- Create: `src/Codesearch.Interop/Codesearch.Interop.csproj`
- Create: `Directory.Build.props`

**Step 1: Create solution and projects**

```bash
dotnet new sln -n codesearch
mkdir -p src/Codesearch.Server src/Codesearch.Interop
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Create `src/Codesearch.Interop/Codesearch.Interop.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

Create placeholder `src/Codesearch.Interop/CodeSearchEngine.cs`:

```csharp
namespace Codesearch.Interop;

/// <summary>
/// Placeholder for UniFFI-generated bindings.
/// This file will be replaced by generated code.
/// </summary>
public static class Placeholder
{
    public static string Message => "UniFFI bindings not yet generated";
}
```

Create `src/Codesearch.Server/Codesearch.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Codesearch.Interop\Codesearch.Interop.csproj" />
  </ItemGroup>
</Project>
```

Create `src/Codesearch.Server/Program.cs`:

```csharp
namespace Codesearch.Server;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Codesearch Server");
        Console.WriteLine($"Interop status: {Interop.Placeholder.Message}");
    }
}
```

**Step 2: Add projects to solution**

```bash
dotnet sln add src/Codesearch.Server/Codesearch.Server.csproj
dotnet sln add src/Codesearch.Interop/Codesearch.Interop.csproj
```

**Step 3: Verify .NET builds**

```bash
dotnet build
```

Expected: Successful build.

**Step 4: Run the server**

```bash
dotnet run --project src/Codesearch.Server
```

Expected output:
```
Codesearch Server
Interop status: UniFFI bindings not yet generated
```

**Step 5: Commit**

```bash
git add codesearch.sln src/ Directory.Build.props
git commit -m "feat: initialize .NET solution with Server and Interop projects"
```

---

## Task 3: Set Up UniFFI C# Binding Generation

**Files:**
- Create: `scripts/generate-bindings.sh`
- Modify: `rust/codesearch-ffi/Cargo.toml` (add bindgen feature)

**Step 1: Install uniffi-bindgen-cs**

```bash
cargo install uniffi-bindgen-cs --git https://github.com/AcylSilane/uniffi-bindgen-cs --tag v0.9.1+v0.28.3
```

Note: The tag matches our uniffi version (0.28).

**Step 2: Create binding generation script**

Create `scripts/generate-bindings.sh`:

```bash
#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

echo "Building Rust FFI library..."
cd "$PROJECT_ROOT/rust"
cargo build --release -p codesearch-ffi

echo "Generating C# bindings..."
uniffi-bindgen-cs \
    --library target/release/libcodesearch_ffi.dylib \
    --out-dir "$PROJECT_ROOT/src/Codesearch.Interop/Generated"

echo "Bindings generated at src/Codesearch.Interop/Generated/"
```

Make it executable:

```bash
chmod +x scripts/generate-bindings.sh
```

**Step 3: Update Interop project to include generated files**

Update `src/Codesearch.Interop/Codesearch.Interop.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="Generated/**/*.cs" Condition="Exists('Generated')" />
  </ItemGroup>
</Project>
```

**Step 4: Create .gitignore for generated files (but track them for now)**

Add to `.gitignore`:

```
# Generated bindings (uncomment if you want to regenerate each build)
# src/Codesearch.Interop/Generated/
```

**Step 5: Commit**

```bash
git add scripts/ src/Codesearch.Interop/Codesearch.Interop.csproj
git commit -m "feat: add UniFFI C# binding generation script"
```

---

## Task 4: Generate and Test UniFFI Bindings

**Files:**
- Create: `src/Codesearch.Interop/Generated/*.cs` (generated)
- Modify: `src/Codesearch.Server/Program.cs`
- Create: `tests/Codesearch.Tests/Codesearch.Tests.csproj`
- Create: `tests/Codesearch.Tests/EngineTests.cs`

**Step 1: Generate bindings**

```bash
./scripts/generate-bindings.sh
```

Expected: Files created in `src/Codesearch.Interop/Generated/`

**Step 2: Copy native library to output**

Create `src/Codesearch.Interop/Codesearch.Interop.csproj` (updated):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="Generated/**/*.cs" Condition="Exists('Generated')" />
  </ItemGroup>

  <!-- Copy native library to output -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <None Include="../../rust/target/release/libcodesearch_ffi.dylib"
          CopyToOutputDirectory="PreserveNewest"
          Link="libcodesearch_ffi.dylib"
          Condition="Exists('../../rust/target/release/libcodesearch_ffi.dylib')" />
  </ItemGroup>

  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <None Include="../../rust/target/release/libcodesearch_ffi.so"
          CopyToOutputDirectory="PreserveNewest"
          Link="libcodesearch_ffi.so"
          Condition="Exists('../../rust/target/release/libcodesearch_ffi.so')" />
  </ItemGroup>

  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <None Include="../../rust/target/release/codesearch_ffi.dll"
          CopyToOutputDirectory="PreserveNewest"
          Link="codesearch_ffi.dll"
          Condition="Exists('../../rust/target/release/codesearch_ffi.dll')" />
  </ItemGroup>
</Project>
```

**Step 3: Create test project**

```bash
mkdir -p tests/Codesearch.Tests
```

Create `tests/Codesearch.Tests/Codesearch.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/Codesearch.Interop/Codesearch.Interop.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/Codesearch.Tests/EngineTests.cs`:

```csharp
using Xunit;
using uniffi.codesearch;

namespace Codesearch.Tests;

public class EngineTests : IDisposable
{
    private readonly string _tempDir;

    public EngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void CanCreateEngine()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");

        var engine = new CodeSearchEngine(dbPath);

        Assert.Equal(dbPath, engine.DbPath());
    }

    [Fact]
    public void HealthCheckReturnsTrue()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        var engine = new CodeSearchEngine(dbPath);

        var healthy = engine.HealthCheck();

        Assert.True(healthy);
    }
}
```

**Step 4: Add test project to solution**

```bash
dotnet sln add tests/Codesearch.Tests/Codesearch.Tests.csproj
```

**Step 5: Build everything**

```bash
cd rust && cargo build --release && cd ..
./scripts/generate-bindings.sh
dotnet build
```

**Step 6: Run tests**

```bash
dotnet test
```

Expected: 2 tests pass.

**Step 7: Update Program.cs to use real engine**

Update `src/Codesearch.Server/Program.cs`:

```csharp
using uniffi.codesearch;

namespace Codesearch.Server;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Codesearch Server");

        var tempPath = Path.Combine(Path.GetTempPath(), "codesearch_demo.lance");

        try
        {
            var engine = new CodeSearchEngine(tempPath);
            Console.WriteLine($"Engine created at: {engine.DbPath()}");
            Console.WriteLine($"Health check: {engine.HealthCheck()}");
        }
        catch (CodeSearchException ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
```

**Step 8: Run the server**

```bash
dotnet run --project src/Codesearch.Server
```

Expected output:
```
Codesearch Server
Engine created at: /tmp/codesearch_demo.lance
Health check: True
```

**Step 9: Commit**

```bash
git add src/ tests/ .gitignore
git commit -m "feat: integrate UniFFI bindings with working .NET-to-Rust interop"
```

---

## Task 5: Add Basic LanceDB Schema

**Files:**
- Create: `rust/codesearch-core/src/schema.rs`
- Modify: `rust/codesearch-core/src/lib.rs`
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Create schema module**

Create `rust/codesearch-core/src/schema.rs`:

```rust
//! LanceDB schema definitions for code symbols

use arrow::datatypes::{DataType, Field, Schema};
use serde::{Deserialize, Serialize};
use std::sync::Arc;

/// Symbol kinds (matches julie-extractors)
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum SymbolKind {
    Function,
    Method,
    Class,
    Interface,
    Struct,
    Enum,
    EnumMember,
    Trait,
    Type,
    Module,
    Namespace,
    Variable,
    Constant,
    Property,
    Field,
    Constructor,
    Import,
    Export,
    File,
    Checkpoint,
    Plan,
    Decision,
    Learning,
}

impl std::fmt::Display for SymbolKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = serde_json::to_string(self).unwrap_or_default();
        write!(f, "{}", s.trim_matches('"'))
    }
}

/// A searchable symbol (code or memory)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Symbol {
    pub id: String,
    pub name: String,
    pub kind: SymbolKind,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub code_pattern: String,
    pub content: Option<String>,
    // Vector will be added separately during insertion
}

impl Symbol {
    /// Generate the code_pattern field for FTS indexing
    pub fn generate_code_pattern(&self) -> String {
        let mut parts = Vec::new();
        if let Some(ref sig) = self.signature {
            parts.push(sig.clone());
        }
        parts.push(self.name.clone());
        parts.push(self.kind.to_string());
        parts.join(" ")
    }
}

/// Vector dimension for embeddings (nomic-embed-text-v1.5)
pub const VECTOR_DIMENSION: usize = 768;

/// Create the Arrow schema for the symbols table
pub fn symbols_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("id", DataType::Utf8, false),
        Field::new("name", DataType::Utf8, false),
        Field::new("kind", DataType::Utf8, false),
        Field::new("language", DataType::Utf8, false),
        Field::new("file_path", DataType::Utf8, false),
        Field::new("signature", DataType::Utf8, true),
        Field::new("doc_comment", DataType::Utf8, true),
        Field::new("start_line", DataType::Int32, true),
        Field::new("end_line", DataType::Int32, true),
        Field::new("code_pattern", DataType::Utf8, false),
        Field::new("content", DataType::Utf8, true),
        Field::new(
            "vector",
            DataType::FixedSizeList(
                Arc::new(Field::new("item", DataType::Float32, false)),
                VECTOR_DIMENSION as i32,
            ),
            false,
        ),
    ]))
}

pub const TABLE_NAME: &str = "symbols";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_symbol_kind_display() {
        assert_eq!(SymbolKind::Function.to_string(), "function");
        assert_eq!(SymbolKind::EnumMember.to_string(), "enum_member");
    }

    #[test]
    fn test_generate_code_pattern() {
        let symbol = Symbol {
            id: "test".into(),
            name: "authenticate".into(),
            kind: SymbolKind::Function,
            language: "rust".into(),
            file_path: "src/auth.rs".into(),
            signature: Some("fn authenticate(token: &str) -> Result<User>".into()),
            doc_comment: None,
            start_line: Some(10),
            end_line: Some(20),
            code_pattern: String::new(),
            content: None,
        };

        let pattern = symbol.generate_code_pattern();
        assert!(pattern.contains("authenticate"));
        assert!(pattern.contains("function"));
        assert!(pattern.contains("fn authenticate"));
    }

    #[test]
    fn test_schema_has_correct_fields() {
        let schema = symbols_schema();
        assert_eq!(schema.fields().len(), 12);
        assert!(schema.field_with_name("id").is_ok());
        assert!(schema.field_with_name("vector").is_ok());
    }
}
```

**Step 2: Update lib.rs**

Update `rust/codesearch-core/src/lib.rs`:

```rust
//! Codesearch core library - search engine powered by LanceDB

pub mod engine;
pub mod error;
pub mod schema;

pub use engine::CodeEngine;
pub use error::Error;
pub use schema::{Symbol, SymbolKind, VECTOR_DIMENSION};

pub type Result<T> = std::result::Result<T, Error>;
```

**Step 3: Run tests**

```bash
cd rust && cargo test
```

Expected: All tests pass (including new schema tests).

**Step 4: Commit**

```bash
git add rust/codesearch-core/src/
git commit -m "feat: add LanceDB schema with Symbol types and Arrow schema"
```

---

## Task 6: Add Symbol Storage and Retrieval

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`
- Modify: `rust/codesearch-ffi/src/lib.rs`
- Modify: `rust/codesearch-ffi/src/codesearch.udl`
- Modify: `tests/Codesearch.Tests/EngineTests.cs`

**Step 1: Add storage methods to engine**

Update `rust/codesearch-core/src/engine.rs`:

```rust
//! Core search engine wrapping LanceDB

use crate::schema::{symbols_schema, Symbol, TABLE_NAME, VECTOR_DIMENSION};
use crate::{Error, Result};
use arrow::array::{
    ArrayRef, FixedSizeListArray, Float32Array, Int32Array, RecordBatch, StringArray,
};
use arrow::datatypes::Field;
use lancedb::query::ExecutableQuery;
use std::path::Path;
use std::sync::Arc;

/// The main search engine struct
pub struct CodeEngine {
    db: Arc<lancedb::Connection>,
    db_path: String,
}

impl CodeEngine {
    /// Create a new CodeEngine with the given database path
    pub async fn new(db_path: &str) -> Result<Self> {
        if db_path != ":memory:" {
            if let Some(parent) = Path::new(db_path).parent() {
                std::fs::create_dir_all(parent)?;
            }
        }

        let db = lancedb::connect(db_path).execute().await?;

        Ok(Self {
            db: Arc::new(db),
            db_path: db_path.to_string(),
        })
    }

    /// Get the database path
    pub fn db_path(&self) -> &str {
        &self.db_path
    }

    /// Check if the engine is healthy
    pub async fn health_check(&self) -> Result<bool> {
        let _tables = self.db.table_names().execute().await?;
        Ok(true)
    }

    /// Add symbols with their embedding vectors
    pub async fn add_symbols(&self, symbols: Vec<Symbol>, vectors: Vec<Vec<f32>>) -> Result<usize> {
        if symbols.is_empty() {
            return Ok(0);
        }

        if symbols.len() != vectors.len() {
            return Err(Error::LanceDb(
                "Symbols and vectors must have same length".into(),
            ));
        }

        // Validate vector dimensions
        for (i, vec) in vectors.iter().enumerate() {
            if vec.len() != VECTOR_DIMENSION {
                return Err(Error::LanceDb(format!(
                    "Vector {} has dimension {}, expected {}",
                    i,
                    vec.len(),
                    VECTOR_DIMENSION
                )));
            }
        }

        let batch = self.symbols_to_record_batch(&symbols, &vectors)?;
        let count = batch.num_rows();

        // Check if table exists
        let table_names = self.db.table_names().execute().await?;

        if table_names.contains(&TABLE_NAME.to_string()) {
            let table = self.db.open_table(TABLE_NAME).execute().await?;
            table.add(Box::new(vec![batch])).execute().await?;
        } else {
            self.db
                .create_table(TABLE_NAME, Box::new(vec![batch]))
                .execute()
                .await?;
        }

        Ok(count)
    }

    /// Get the count of symbols in the database
    pub async fn symbol_count(&self) -> Result<usize> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(0);
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;
        let count = table.count_rows(None).await?;
        Ok(count)
    }

    fn symbols_to_record_batch(
        &self,
        symbols: &[Symbol],
        vectors: &[Vec<f32>],
    ) -> Result<RecordBatch> {
        let ids: Vec<&str> = symbols.iter().map(|s| s.id.as_str()).collect();
        let names: Vec<&str> = symbols.iter().map(|s| s.name.as_str()).collect();
        let kinds: Vec<String> = symbols.iter().map(|s| s.kind.to_string()).collect();
        let languages: Vec<&str> = symbols.iter().map(|s| s.language.as_str()).collect();
        let file_paths: Vec<&str> = symbols.iter().map(|s| s.file_path.as_str()).collect();
        let signatures: Vec<Option<&str>> = symbols.iter().map(|s| s.signature.as_deref()).collect();
        let doc_comments: Vec<Option<&str>> =
            symbols.iter().map(|s| s.doc_comment.as_deref()).collect();
        let start_lines: Vec<Option<i32>> = symbols.iter().map(|s| s.start_line).collect();
        let end_lines: Vec<Option<i32>> = symbols.iter().map(|s| s.end_line).collect();
        let code_patterns: Vec<String> =
            symbols.iter().map(|s| s.generate_code_pattern()).collect();
        let contents: Vec<Option<&str>> = symbols.iter().map(|s| s.content.as_deref()).collect();

        // Build fixed-size list array for vectors
        let flat_values: Vec<f32> = vectors.iter().flatten().copied().collect();
        let values_array = Float32Array::from(flat_values);
        let field = Arc::new(Field::new("item", arrow::datatypes::DataType::Float32, false));
        let vector_array =
            FixedSizeListArray::new(field, VECTOR_DIMENSION as i32, Arc::new(values_array), None);

        let batch = RecordBatch::try_new(
            symbols_schema(),
            vec![
                Arc::new(StringArray::from(ids)) as ArrayRef,
                Arc::new(StringArray::from(names)) as ArrayRef,
                Arc::new(StringArray::from(kinds.iter().map(|s| s.as_str()).collect::<Vec<_>>()))
                    as ArrayRef,
                Arc::new(StringArray::from(languages)) as ArrayRef,
                Arc::new(StringArray::from(file_paths)) as ArrayRef,
                Arc::new(StringArray::from(signatures)) as ArrayRef,
                Arc::new(StringArray::from(doc_comments)) as ArrayRef,
                Arc::new(Int32Array::from(start_lines)) as ArrayRef,
                Arc::new(Int32Array::from(end_lines)) as ArrayRef,
                Arc::new(StringArray::from(
                    code_patterns.iter().map(|s| s.as_str()).collect::<Vec<_>>(),
                )) as ArrayRef,
                Arc::new(StringArray::from(contents)) as ArrayRef,
                Arc::new(vector_array) as ArrayRef,
            ],
        )
        .map_err(|e| Error::LanceDb(e.to_string()))?;

        Ok(batch)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::SymbolKind;
    use tempfile::TempDir;

    fn create_test_symbol(name: &str) -> Symbol {
        Symbol {
            id: format!("test_{}", name),
            name: name.to_string(),
            kind: SymbolKind::Function,
            language: "rust".to_string(),
            file_path: "src/test.rs".to_string(),
            signature: Some(format!("fn {}()", name)),
            doc_comment: None,
            start_line: Some(1),
            end_line: Some(10),
            code_pattern: String::new(),
            content: None,
        }
    }

    fn create_test_vector() -> Vec<f32> {
        vec![0.1; VECTOR_DIMENSION]
    }

    #[tokio::test]
    async fn test_engine_creation() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();
        assert!(engine.health_check().await.unwrap());
    }

    #[tokio::test]
    async fn test_add_symbols() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");
        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let symbols = vec![create_test_symbol("foo"), create_test_symbol("bar")];
        let vectors = vec![create_test_vector(), create_test_vector()];

        let count = engine.add_symbols(symbols, vectors).await.unwrap();
        assert_eq!(count, 2);
    }

    #[tokio::test]
    async fn test_symbol_count() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");
        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        assert_eq!(engine.symbol_count().await.unwrap(), 0);

        let symbols = vec![create_test_symbol("foo")];
        let vectors = vec![create_test_vector()];
        engine.add_symbols(symbols, vectors).await.unwrap();

        assert_eq!(engine.symbol_count().await.unwrap(), 1);
    }
}
```

**Step 2: Run Rust tests**

```bash
cd rust && cargo test
```

Expected: All tests pass.

**Step 3: Update FFI to expose new methods**

Update `rust/codesearch-ffi/src/codesearch.udl`:

```
namespace codesearch {};

[Error]
enum CodeSearchError {
    "Database",
    "Runtime",
};

dictionary SymbolInput {
    string id;
    string name;
    string kind;
    string language;
    string file_path;
    string? signature;
    string? doc_comment;
    i32? start_line;
    i32? end_line;
    string? content;
};

interface CodeSearchEngine {
    [Throws=CodeSearchError]
    constructor(string db_path);

    string db_path();

    [Throws=CodeSearchError]
    boolean health_check();

    [Throws=CodeSearchError]
    u64 add_symbols(sequence<SymbolInput> symbols, sequence<sequence<f32>> vectors);

    [Throws=CodeSearchError]
    u64 symbol_count();
};
```

Update `rust/codesearch-ffi/src/lib.rs`:

```rust
//! UniFFI bindings for codesearch-core

use codesearch_core::{Symbol, SymbolKind};
use std::sync::Arc;
use tokio::runtime::Runtime;

uniffi::setup_scaffolding!();

/// FFI-safe symbol input
#[derive(uniffi::Record)]
pub struct SymbolInput {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub content: Option<String>,
}

impl From<SymbolInput> for Symbol {
    fn from(input: SymbolInput) -> Self {
        let kind = match input.kind.as_str() {
            "function" => SymbolKind::Function,
            "method" => SymbolKind::Method,
            "class" => SymbolKind::Class,
            "interface" => SymbolKind::Interface,
            "struct" => SymbolKind::Struct,
            "enum" => SymbolKind::Enum,
            "trait" => SymbolKind::Trait,
            "type" => SymbolKind::Type,
            "module" => SymbolKind::Module,
            "namespace" => SymbolKind::Namespace,
            "variable" => SymbolKind::Variable,
            "constant" => SymbolKind::Constant,
            "property" => SymbolKind::Property,
            "field" => SymbolKind::Field,
            "constructor" => SymbolKind::Constructor,
            "import" => SymbolKind::Import,
            "export" => SymbolKind::Export,
            "file" => SymbolKind::File,
            "checkpoint" => SymbolKind::Checkpoint,
            "plan" => SymbolKind::Plan,
            "decision" => SymbolKind::Decision,
            "learning" => SymbolKind::Learning,
            _ => SymbolKind::Function, // Default
        };

        Symbol {
            id: input.id,
            name: input.name,
            kind,
            language: input.language,
            file_path: input.file_path,
            signature: input.signature,
            doc_comment: input.doc_comment,
            start_line: input.start_line,
            end_line: input.end_line,
            code_pattern: String::new(), // Will be generated
            content: input.content,
        }
    }
}

/// FFI-safe wrapper around CodeEngine
#[derive(uniffi::Object)]
pub struct CodeSearchEngine {
    inner: codesearch_core::CodeEngine,
    runtime: Arc<Runtime>,
}

#[uniffi::export]
impl CodeSearchEngine {
    #[uniffi::constructor]
    pub fn new(db_path: String) -> Result<Arc<Self>, CodeSearchError> {
        let runtime =
            Arc::new(Runtime::new().map_err(|e| CodeSearchError::Runtime(e.to_string()))?);

        let inner = runtime.block_on(async { codesearch_core::CodeEngine::new(&db_path).await })?;

        Ok(Arc::new(Self { inner, runtime }))
    }

    pub fn db_path(&self) -> String {
        self.inner.db_path().to_string()
    }

    pub fn health_check(&self) -> Result<bool, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .health_check()
                .await
                .map_err(CodeSearchError::from)
        })
    }

    pub fn add_symbols(
        &self,
        symbols: Vec<SymbolInput>,
        vectors: Vec<Vec<f32>>,
    ) -> Result<u64, CodeSearchError> {
        let symbols: Vec<Symbol> = symbols.into_iter().map(Symbol::from).collect();

        self.runtime.block_on(async {
            self.inner
                .add_symbols(symbols, vectors)
                .await
                .map(|n| n as u64)
                .map_err(CodeSearchError::from)
        })
    }

    pub fn symbol_count(&self) -> Result<u64, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .symbol_count()
                .await
                .map(|n| n as u64)
                .map_err(CodeSearchError::from)
        })
    }
}

/// FFI-safe error type
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum CodeSearchError {
    #[error("Database error: {0}")]
    Database(String),

    #[error("Runtime error: {0}")]
    Runtime(String),
}

impl From<codesearch_core::Error> for CodeSearchError {
    fn from(e: codesearch_core::Error) -> Self {
        CodeSearchError::Database(e.to_string())
    }
}
```

**Step 4: Build and regenerate bindings**

```bash
cd rust && cargo build --release && cd ..
./scripts/generate-bindings.sh
```

**Step 5: Update .NET tests**

Update `tests/Codesearch.Tests/EngineTests.cs`:

```csharp
using Xunit;
using uniffi.codesearch;

namespace Codesearch.Tests;

public class EngineTests : IDisposable
{
    private readonly string _tempDir;

    public EngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [Fact]
    public void CanCreateEngine()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");

        var engine = new CodeSearchEngine(dbPath);

        Assert.Equal(dbPath, engine.DbPath());
    }

    [Fact]
    public void HealthCheckReturnsTrue()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        var engine = new CodeSearchEngine(dbPath);

        var healthy = engine.HealthCheck();

        Assert.True(healthy);
    }

    [Fact]
    public void CanAddSymbols()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        var engine = new CodeSearchEngine(dbPath);

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_foo",
                name: "foo",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn foo()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        // Create a dummy vector (768 dimensions for nomic-embed)
        var vector = Enumerable.Repeat(0.1f, 768).ToList();
        var vectors = new List<List<float>> { vector };

        var count = engine.AddSymbols(symbols, vectors);

        Assert.Equal(1UL, count);
    }

    [Fact]
    public void CanGetSymbolCount()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        var engine = new CodeSearchEngine(dbPath);

        Assert.Equal(0UL, engine.SymbolCount());

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_foo",
                name: "foo",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn foo()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        var vector = Enumerable.Repeat(0.1f, 768).ToList();
        var vectors = new List<List<float>> { vector };

        engine.AddSymbols(symbols, vectors);

        Assert.Equal(1UL, engine.SymbolCount());
    }
}
```

**Step 6: Run .NET tests**

```bash
dotnet test
```

Expected: All 4 tests pass.

**Step 7: Commit**

```bash
git add rust/ tests/ src/
git commit -m "feat: add symbol storage and retrieval via LanceDB"
```

---

## Summary

Phase 1 establishes:

1. **Rust workspace** with `codesearch-core` (engine) and `codesearch-ffi` (bindings)
2. **.NET solution** with `Codesearch.Server` and `Codesearch.Interop`
3. **UniFFI interop** generating C# bindings from Rust
4. **LanceDB integration** with Arrow schema for symbols
5. **Working tests** proving end-to-end .NET → Rust → LanceDB flow

**Next phase** will add:
- Tree-sitter extractors (copy from julie)
- Vector search
- FTS index with Tantivy
