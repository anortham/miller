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
