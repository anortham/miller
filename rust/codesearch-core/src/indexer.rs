//! File indexing - extract symbols and store in database.
//!
//! NOTE: Extraction functionality temporarily disabled during migration
//! from codesearch-extractors to julie-extractors. Will be restored in
//! subsequent tasks.

use crate::{engine::CodeEngine, Error, Result};
use std::path::Path;

impl CodeEngine {
    /// Index a single file - extract symbols and store with placeholder vectors.
    ///
    /// Note: This stores symbols with zero vectors. Real embeddings should be
    /// provided via index_file_with_embeddings() for semantic search.
    ///
    /// TODO: Re-enable once julie-extractors integration is complete.
    pub async fn index_file(
        &self,
        _file_path: &str,
        _content: &str,
        _workspace_root: &Path,
    ) -> Result<usize> {
        // Temporarily disabled during extractor migration
        Err(Error::Validation(
            "Extraction temporarily disabled - migration to julie-extractors in progress".to_string()
        ))
    }

    /// Index a file with provided embeddings.
    ///
    /// TODO: Re-enable once julie-extractors integration is complete.
    pub async fn index_file_with_embeddings(
        &self,
        _file_path: &str,
        _content: &str,
        _workspace_root: &Path,
        _embeddings: Vec<Vec<f32>>,
    ) -> Result<usize> {
        // Temporarily disabled during extractor migration
        Err(Error::Validation(
            "Extraction temporarily disabled - migration to julie-extractors in progress".to_string()
        ))
    }
}

// Will be used when julie-extractors integration is complete
#[allow(dead_code)]
fn generate_code_pattern(name: &str, signature: Option<&str>) -> String {
    match signature {
        Some(sig) => format!("{} {}", sig, name),
        None => name.to_string(),
    }
}

// Tests temporarily disabled during extractor migration
#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn test_extraction_disabled() {
        // Placeholder - full tests will be restored with julie-extractors
    }
}
