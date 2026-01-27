//! File indexing - extract symbols and store in database.

use crate::{engine::CodeEngine, schema::Symbol, Error, Result, VECTOR_DIMENSION};
use codesearch_extractors::ExtractorManager;
use std::path::Path;

impl CodeEngine {
    /// Index a single file - extract symbols and store with placeholder vectors.
    ///
    /// Note: This stores symbols with zero vectors. Real embeddings should be
    /// provided via index_file_with_embeddings() for semantic search.
    pub async fn index_file(
        &self,
        file_path: &str,
        content: &str,
        workspace_root: &Path,
    ) -> Result<usize> {
        let extracted = ExtractorManager::extract_symbols(file_path, content, workspace_root)
            .map_err(|e| Error::Validation(e.to_string()))?;

        if extracted.is_empty() {
            return Ok(0);
        }

        // Convert extractor symbols to our schema symbols
        let symbols: Vec<Symbol> = extracted
            .into_iter()
            .map(|s| self.convert_extracted_symbol(s))
            .collect();

        // Placeholder vectors (768 zeros) - caller should provide real embeddings
        let vectors: Vec<Vec<f32>> = vec![vec![0.0; VECTOR_DIMENSION]; symbols.len()];

        self.add_symbols(symbols, vectors).await
    }

    /// Index a file with provided embeddings.
    pub async fn index_file_with_embeddings(
        &self,
        file_path: &str,
        content: &str,
        workspace_root: &Path,
        embeddings: Vec<Vec<f32>>,
    ) -> Result<usize> {
        let extracted = ExtractorManager::extract_symbols(file_path, content, workspace_root)
            .map_err(|e| Error::Validation(e.to_string()))?;

        if extracted.is_empty() {
            return Ok(0);
        }

        if extracted.len() != embeddings.len() {
            return Err(Error::Validation(format!(
                "Embedding count ({}) doesn't match symbol count ({})",
                embeddings.len(),
                extracted.len()
            )));
        }

        let symbols: Vec<Symbol> = extracted
            .into_iter()
            .map(|s| self.convert_extracted_symbol(s))
            .collect();

        self.add_symbols(symbols, embeddings).await
    }

    fn convert_extracted_symbol(&self, s: codesearch_extractors::Symbol) -> Symbol {
        Symbol {
            id: s.id,
            name: s.name.clone(),
            kind: convert_symbol_kind(s.kind),
            language: s.language,
            file_path: s.file_path,
            signature: s.signature.clone(),
            doc_comment: s.doc_comment,
            start_line: Some(s.start_line as i32),
            end_line: Some(s.end_line as i32),
            code_pattern: generate_code_pattern(&s.name, s.signature.as_deref()),
            content: None,
        }
    }
}

fn convert_symbol_kind(kind: codesearch_extractors::SymbolKind) -> crate::schema::SymbolKind {
    use codesearch_extractors::SymbolKind as ExtKind;
    use crate::schema::SymbolKind;

    match kind {
        ExtKind::Function => SymbolKind::Function,
        ExtKind::Method => SymbolKind::Method,
        ExtKind::Class => SymbolKind::Class,
        ExtKind::Struct => SymbolKind::Struct,
        ExtKind::Interface => SymbolKind::Interface,
        ExtKind::Trait => SymbolKind::Trait,
        ExtKind::Enum => SymbolKind::Enum,
        ExtKind::EnumMember => SymbolKind::EnumMember,
        ExtKind::Variable => SymbolKind::Variable,
        ExtKind::Constant => SymbolKind::Constant,
        ExtKind::Property => SymbolKind::Property,
        ExtKind::Field => SymbolKind::Field,
        ExtKind::Module => SymbolKind::Module,
        ExtKind::Namespace => SymbolKind::Namespace,
        ExtKind::Type => SymbolKind::Type,
        ExtKind::Constructor => SymbolKind::Constructor,
        ExtKind::Import => SymbolKind::Import,
        ExtKind::Export => SymbolKind::Export,
        _ => SymbolKind::Function, // Fallback for unmapped kinds (Union, Destructor, Operator, Event, Delegate)
    }
}

fn generate_code_pattern(name: &str, signature: Option<&str>) -> String {
    match signature {
        Some(sig) => format!("{} {}", sig, name),
        None => name.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_index_rust_file() {
        let dir = tempfile::tempdir().unwrap();
        let db_path = dir.path().join("test.lance");
        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let code = r#"
pub fn hello() {
    println!("Hello");
}

pub struct User {
    name: String,
}
"#;

        let count = engine
            .index_file("test.rs", code, Path::new("/workspace"))
            .await
            .unwrap();

        // Should extract function + struct + field
        assert!(count >= 2);
        assert_eq!(engine.symbol_count().await.unwrap(), count);
    }

    #[tokio::test]
    async fn test_index_unsupported_file() {
        let dir = tempfile::tempdir().unwrap();
        let db_path = dir.path().join("test.lance");
        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let result = engine
            .index_file("test.xyz", "content", Path::new("/workspace"))
            .await;

        assert!(result.is_err());
    }
}
