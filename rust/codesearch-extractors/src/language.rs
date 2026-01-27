//! Language detection and tree-sitter configuration.

use anyhow::{anyhow, Result};

/// Detect language from file extension.
pub fn detect_language(extension: &str) -> Option<&'static str> {
    match extension.to_lowercase().as_str() {
        "rs" => Some("rust"),
        "ts" | "tsx" => Some("typescript"),
        "js" | "jsx" | "mjs" | "cjs" => Some("javascript"),
        "py" | "pyw" | "pyi" => Some("python"),
        _ => None,
    }
}

/// Get tree-sitter language for parsing.
pub fn get_tree_sitter_language(language: &str) -> Result<tree_sitter::Language> {
    match language {
        "rust" => Ok(tree_sitter_rust::LANGUAGE.into()),
        "typescript" | "tsx" => Ok(tree_sitter_typescript::LANGUAGE_TYPESCRIPT.into()),
        "javascript" | "jsx" => Ok(tree_sitter_typescript::LANGUAGE_TYPESCRIPT.into()),
        "python" => Ok(tree_sitter_python::LANGUAGE.into()),
        _ => Err(anyhow!("Unsupported language: {}", language)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_detect_language() {
        assert_eq!(detect_language("rs"), Some("rust"));
        assert_eq!(detect_language("RS"), Some("rust")); // Case insensitive
        assert_eq!(detect_language("ts"), Some("typescript"));
        assert_eq!(detect_language("tsx"), Some("typescript"));
        assert_eq!(detect_language("js"), Some("javascript"));
        assert_eq!(detect_language("py"), Some("python"));
        assert_eq!(detect_language("unknown"), None);
    }

    #[test]
    fn test_get_tree_sitter_language() {
        assert!(get_tree_sitter_language("rust").is_ok());
        assert!(get_tree_sitter_language("typescript").is_ok());
        assert!(get_tree_sitter_language("javascript").is_ok());
        assert!(get_tree_sitter_language("python").is_ok());
        assert!(get_tree_sitter_language("unknown").is_err());
    }
}
