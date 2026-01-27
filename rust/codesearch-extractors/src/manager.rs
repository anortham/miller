//! High-level extraction API.

use crate::{
    base::Symbol,
    language::{detect_language, get_tree_sitter_language},
    python::PythonExtractor,
    rust::RustExtractor,
    typescript::TypeScriptExtractor,
};
use anyhow::{anyhow, Result};
use std::path::Path;

/// High-level manager for extracting symbols from source files.
pub struct ExtractorManager;

impl ExtractorManager {
    /// Extract symbols from a file.
    ///
    /// # Arguments
    /// * `file_path` - Path to the source file (relative to workspace)
    /// * `content` - File content
    /// * `workspace_root` - Root directory of the workspace
    ///
    /// # Returns
    /// Vector of extracted symbols, or error if language unsupported.
    pub fn extract_symbols(
        file_path: &str,
        content: &str,
        workspace_root: &Path,
    ) -> Result<Vec<Symbol>> {
        let extension = Path::new(file_path)
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("");

        let language = detect_language(extension)
            .ok_or_else(|| anyhow!("Unsupported file extension: {}", extension))?;

        let tree_sitter_lang = get_tree_sitter_language(language)?;

        let mut parser = tree_sitter::Parser::new();
        parser
            .set_language(&tree_sitter_lang)
            .map_err(|e| anyhow!("Failed to set language: {}", e))?;

        let tree = parser
            .parse(content, None)
            .ok_or_else(|| anyhow!("Failed to parse file"))?;

        let symbols = match language {
            "rust" => {
                let mut extractor = RustExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "typescript" | "javascript" => {
                let mut extractor = TypeScriptExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "python" => {
                let mut extractor = PythonExtractor::new(
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            _ => return Err(anyhow!("No extractor for language: {}", language)),
        };

        Ok(symbols)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_extract_rust_file() {
        let code = "pub fn hello() {}";
        let symbols = ExtractorManager::extract_symbols(
            "test.rs",
            code,
            Path::new("/workspace"),
        ).unwrap();

        assert_eq!(symbols.len(), 1);
        assert_eq!(symbols[0].name, "hello");
    }

    #[test]
    fn test_extract_typescript_file() {
        let code = "export function greet() {}";
        let symbols = ExtractorManager::extract_symbols(
            "test.ts",
            code,
            Path::new("/workspace"),
        ).unwrap();

        assert!(!symbols.is_empty());
    }

    #[test]
    fn test_extract_python_file() {
        let code = "def main():\n    pass";
        let symbols = ExtractorManager::extract_symbols(
            "test.py",
            code,
            Path::new("/workspace"),
        ).unwrap();

        assert!(!symbols.is_empty());
    }

    #[test]
    fn test_unsupported_extension() {
        let result = ExtractorManager::extract_symbols(
            "test.xyz",
            "content",
            Path::new("/workspace"),
        );

        assert!(result.is_err());
    }
}
