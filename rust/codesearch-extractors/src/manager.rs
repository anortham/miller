//! High-level extraction API.
//!
//! Provides a unified interface for extracting symbols from source files
//! across all 31 supported programming languages.

use crate::{
    base::Symbol,
    language::{detect_language, get_tree_sitter_language},
    bash::BashExtractor,
    c::CExtractor,
    cpp::CppExtractor,
    csharp::CSharpExtractor,
    css::CSSExtractor,
    dart::DartExtractor,
    gdscript::GDScriptExtractor,
    go::GoExtractor,
    html::HTMLExtractor,
    java::JavaExtractor,
    javascript::JavaScriptExtractor,
    json::JsonExtractor,
    kotlin::KotlinExtractor,
    lua::LuaExtractor,
    markdown::MarkdownExtractor,
    php::PhpExtractor,
    powershell::PowerShellExtractor,
    python::PythonExtractor,
    qml::QmlExtractor,
    r::RExtractor,
    razor::RazorExtractor,
    regex::RegexExtractor,
    ruby::RubyExtractor,
    rust::RustExtractor,
    sql::SqlExtractor,
    swift::SwiftExtractor,
    toml::TomlExtractor,
    typescript::TypeScriptExtractor,
    vue::VueExtractor,
    yaml::YamlExtractor,
    zig::ZigExtractor,
};
use anyhow::{anyhow, Result};
use std::path::Path;

/// High-level manager for extracting symbols from source files.
pub struct ExtractorManager;

impl Default for ExtractorManager {
    fn default() -> Self {
        Self::new()
    }
}

impl ExtractorManager {
    /// Create a new extractor manager.
    pub fn new() -> Self {
        Self
    }

    /// Get supported languages (all 31 extractors)
    pub fn supported_languages(&self) -> Vec<&'static str> {
        vec![
            "rust",
            "typescript",
            "tsx",
            "javascript",
            "jsx",
            "python",
            "go",
            "java",
            "c",
            "cpp",
            "csharp",
            "ruby",
            "php",
            "swift",
            "kotlin",
            "dart",
            "gdscript",
            "lua",
            "qml",
            "r",
            "vue",
            "razor",
            "sql",
            "html",
            "css",
            "bash",
            "powershell",
            "zig",
            "regex",
            "markdown",
            "json",
            "toml",
            "yaml",
        ]
    }

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
            // Systems languages
            "rust" => {
                let mut extractor = RustExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "c" => {
                let mut extractor = CExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "cpp" => {
                let mut extractor = CppExtractor::new(
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "go" => {
                let mut extractor = GoExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "zig" => {
                let mut extractor = ZigExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }

            // Web languages
            "typescript" | "tsx" => {
                let mut extractor = TypeScriptExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "javascript" | "jsx" => {
                let mut extractor = JavaScriptExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "html" => {
                let mut extractor = HTMLExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "css" => {
                let mut extractor = CSSExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "vue" => {
                let mut extractor = VueExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(Some(&tree))
            }
            "qml" => {
                let mut extractor = QmlExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }

            // Backend languages
            "python" => {
                let mut extractor = PythonExtractor::new(
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "java" => {
                let mut extractor = JavaExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "csharp" => {
                let mut extractor = CSharpExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "php" => {
                let mut extractor = PhpExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "ruby" => {
                let mut extractor = RubyExtractor::new(
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "swift" => {
                let mut extractor = SwiftExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "kotlin" => {
                let mut extractor = KotlinExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "dart" => {
                let mut extractor = DartExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }

            // Scripting languages
            "lua" => {
                let mut extractor = LuaExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "r" => {
                let mut extractor = RExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "bash" => {
                let mut extractor = BashExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "powershell" => {
                let mut extractor = PowerShellExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }

            // Specialized languages
            "gdscript" => {
                let mut extractor = GDScriptExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "razor" => {
                let mut extractor = RazorExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "sql" => {
                let mut extractor = SqlExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "regex" => {
                let mut extractor = RegexExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }

            // Documentation and configuration languages
            "markdown" => {
                let mut extractor = MarkdownExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "json" => {
                let mut extractor = JsonExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "toml" => {
                let mut extractor = TomlExtractor::new(
                    language.to_string(),
                    file_path.to_string(),
                    content.to_string(),
                    workspace_root,
                );
                extractor.extract_symbols(&tree)
            }
            "yaml" => {
                let mut extractor = YamlExtractor::new(
                    language.to_string(),
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
    fn test_extract_go_file() {
        let code = "package main\n\nfunc hello() {}";
        let symbols = ExtractorManager::extract_symbols(
            "test.go",
            code,
            Path::new("/workspace"),
        ).unwrap();

        assert!(!symbols.is_empty());
    }

    #[test]
    fn test_extract_java_file() {
        let code = "public class Hello { public void greet() {} }";
        let symbols = ExtractorManager::extract_symbols(
            "Hello.java",
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
