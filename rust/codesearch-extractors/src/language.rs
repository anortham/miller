//! Language detection and tree-sitter configuration.
//!
//! This module provides centralized language support for all 31 supported languages.

use anyhow::{anyhow, Result};

/// Detect language from file extension.
pub fn detect_language(extension: &str) -> Option<&'static str> {
    match extension.to_lowercase().as_str() {
        "rs" => Some("rust"),
        "ts" => Some("typescript"),
        "tsx" => Some("tsx"),
        "js" | "jsx" | "mjs" | "cjs" => Some("javascript"),
        "py" | "pyw" | "pyi" => Some("python"),
        "go" => Some("go"),
        "java" => Some("java"),
        "c" | "h" => Some("c"),
        "cpp" | "cc" | "cxx" | "hpp" | "hh" | "hxx" => Some("cpp"),
        "cs" => Some("csharp"),
        "rb" => Some("ruby"),
        "php" => Some("php"),
        "swift" => Some("swift"),
        "kt" | "kts" => Some("kotlin"),
        "dart" => Some("dart"),
        "gd" => Some("gdscript"),
        "lua" => Some("lua"),
        "qml" => Some("qml"),
        "r" => Some("r"),
        "vue" => Some("vue"),
        "razor" | "cshtml" => Some("razor"),
        "sql" => Some("sql"),
        "html" | "htm" => Some("html"),
        "css" => Some("css"),
        "sh" | "bash" => Some("bash"),
        "ps1" => Some("powershell"),
        "zig" => Some("zig"),
        "regex" => Some("regex"),
        "md" | "markdown" => Some("markdown"),
        "json" | "jsonl" | "jsonc" => Some("json"),
        "toml" => Some("toml"),
        "yml" | "yaml" => Some("yaml"),
        _ => None,
    }
}

/// Get tree-sitter language for parsing.
///
/// # Supported Languages (31 total)
///
/// **Systems**: Rust, C, C++, Go, Zig
/// **Web**: TypeScript, JavaScript, HTML, CSS, Vue, QML
/// **Backend**: Python, Java, C#, PHP, Ruby, Swift, Kotlin, Dart
/// **Scripting**: Lua, R, Bash, PowerShell
/// **Specialized**: GDScript, Razor, SQL, Regex
/// **Documentation**: Markdown, JSON, TOML, YAML
pub fn get_tree_sitter_language(language: &str) -> Result<tree_sitter::Language> {
    match language {
        // Systems languages
        "rust" => Ok(tree_sitter_rust::LANGUAGE.into()),
        "c" => Ok(tree_sitter_c::LANGUAGE.into()),
        "cpp" => Ok(tree_sitter_cpp::LANGUAGE.into()),
        "go" => Ok(tree_sitter_go::LANGUAGE.into()),
        "zig" => Ok(tree_sitter_zig::LANGUAGE.into()),

        // Web languages
        "typescript" => Ok(tree_sitter_typescript::LANGUAGE_TYPESCRIPT.into()),
        "tsx" => Ok(tree_sitter_typescript::LANGUAGE_TSX.into()),
        "javascript" | "jsx" => Ok(tree_sitter_javascript::LANGUAGE.into()),
        "html" => Ok(tree_sitter_html::LANGUAGE.into()),
        "css" => Ok(tree_sitter_css::LANGUAGE.into()),
        "vue" => Ok(tree_sitter_html::LANGUAGE.into()), // Vue SFCs use HTML structure

        // Backend languages
        "python" => Ok(tree_sitter_python::LANGUAGE.into()),
        "java" => Ok(tree_sitter_java::LANGUAGE.into()),
        "csharp" => Ok(tree_sitter_c_sharp::LANGUAGE.into()),
        "php" => Ok(tree_sitter_php::LANGUAGE_PHP.into()),
        "ruby" => Ok(tree_sitter_ruby::LANGUAGE.into()),
        "swift" => Ok(tree_sitter_swift::LANGUAGE.into()),
        "kotlin" => Ok(tree_sitter_kotlin_ng::LANGUAGE.into()),
        "dart" => Ok(harper_tree_sitter_dart::LANGUAGE.into()),

        // Scripting languages
        "lua" => Ok(tree_sitter_lua::LANGUAGE.into()),
        "qml" => Ok(tree_sitter_qmljs::LANGUAGE.into()),
        "r" => Ok(tree_sitter_r::LANGUAGE.into()),
        "bash" => Ok(tree_sitter_bash::LANGUAGE.into()),
        "powershell" => Ok(tree_sitter_powershell::LANGUAGE.into()),

        // Specialized languages
        "gdscript" => Ok(tree_sitter_gdscript::LANGUAGE.into()),
        "razor" => Ok(tree_sitter_razor::LANGUAGE.into()),
        "sql" => Ok(tree_sitter_sequel::LANGUAGE.into()),
        "regex" => Ok(tree_sitter_regex::LANGUAGE.into()),

        // Documentation and configuration languages
        "markdown" => Ok(tree_sitter_md::LANGUAGE.into()),
        "json" => Ok(tree_sitter_json::LANGUAGE.into()),
        "toml" => Ok(tree_sitter_toml_ng::LANGUAGE.into()),
        "yaml" => Ok(tree_sitter_yaml::LANGUAGE.into()),

        _ => Err(anyhow!(
            "Unsupported language: '{}'. Supported: rust, c, cpp, go, zig, typescript, javascript, html, css, vue, qml, r, python, java, csharp, php, ruby, swift, kotlin, dart, lua, bash, powershell, gdscript, razor, sql, regex, markdown, json, toml, yaml",
            language
        )),
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
        assert_eq!(detect_language("tsx"), Some("tsx"));
        assert_eq!(detect_language("js"), Some("javascript"));
        assert_eq!(detect_language("py"), Some("python"));
        assert_eq!(detect_language("go"), Some("go"));
        assert_eq!(detect_language("java"), Some("java"));
        assert_eq!(detect_language("c"), Some("c"));
        assert_eq!(detect_language("cpp"), Some("cpp"));
        assert_eq!(detect_language("cs"), Some("csharp"));
        assert_eq!(detect_language("rb"), Some("ruby"));
        assert_eq!(detect_language("php"), Some("php"));
        assert_eq!(detect_language("swift"), Some("swift"));
        assert_eq!(detect_language("kt"), Some("kotlin"));
        assert_eq!(detect_language("dart"), Some("dart"));
        assert_eq!(detect_language("gd"), Some("gdscript"));
        assert_eq!(detect_language("lua"), Some("lua"));
        assert_eq!(detect_language("qml"), Some("qml"));
        assert_eq!(detect_language("r"), Some("r"));
        assert_eq!(detect_language("vue"), Some("vue"));
        assert_eq!(detect_language("razor"), Some("razor"));
        assert_eq!(detect_language("sql"), Some("sql"));
        assert_eq!(detect_language("html"), Some("html"));
        assert_eq!(detect_language("css"), Some("css"));
        assert_eq!(detect_language("sh"), Some("bash"));
        assert_eq!(detect_language("ps1"), Some("powershell"));
        assert_eq!(detect_language("zig"), Some("zig"));
        assert_eq!(detect_language("md"), Some("markdown"));
        assert_eq!(detect_language("json"), Some("json"));
        assert_eq!(detect_language("toml"), Some("toml"));
        assert_eq!(detect_language("yaml"), Some("yaml"));
        assert_eq!(detect_language("unknown"), None);
    }

    #[test]
    fn test_get_tree_sitter_language() {
        assert!(get_tree_sitter_language("rust").is_ok());
        assert!(get_tree_sitter_language("typescript").is_ok());
        assert!(get_tree_sitter_language("javascript").is_ok());
        assert!(get_tree_sitter_language("python").is_ok());
        assert!(get_tree_sitter_language("go").is_ok());
        assert!(get_tree_sitter_language("java").is_ok());
        assert!(get_tree_sitter_language("c").is_ok());
        assert!(get_tree_sitter_language("cpp").is_ok());
        assert!(get_tree_sitter_language("csharp").is_ok());
        assert!(get_tree_sitter_language("unknown").is_err());
    }
}
