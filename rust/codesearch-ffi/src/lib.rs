//! UniFFI bindings for codesearch-core

use codesearch_core::{Symbol, SymbolKind};
use std::sync::Arc;
use tokio::runtime::Runtime;

uniffi::setup_scaffolding!();

/// FFI-safe symbol input (uses strings for all fields to be FFI-friendly)
#[derive(Debug, Clone, uniffi::Record)]
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
            "enum_member" => SymbolKind::EnumMember,
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
            _ => SymbolKind::Function, // Default fallback
        };

        let mut symbol = Symbol {
            id: input.id,
            name: input.name.clone(),
            kind: kind.clone(),
            language: input.language,
            file_path: input.file_path,
            signature: input.signature.clone(),
            doc_comment: input.doc_comment,
            start_line: input.start_line,
            end_line: input.end_line,
            code_pattern: String::new(),
            content: input.content,
        };
        symbol.code_pattern = symbol.generate_code_pattern();
        symbol
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

    /// Add symbols with their embedding vectors to the database
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
                .map(|count| count as u64)
                .map_err(CodeSearchError::from)
        })
    }

    /// Get the count of symbols in the database
    pub fn symbol_count(&self) -> Result<u64, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .symbol_count()
                .await
                .map(|count| count as u64)
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
