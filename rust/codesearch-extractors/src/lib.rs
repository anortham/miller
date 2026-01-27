//! Tree-sitter based symbol extraction for codesearch.
//!
//! Cross-platform code intelligence extractors for 31 programming languages.
//!
//! # Supported Languages (31 total)
//!
//! **Systems**: Rust, C, C++, Go, Zig
//! **Web**: TypeScript, JavaScript, HTML, CSS, Vue, QML
//! **Backend**: Python, Java, C#, PHP, Ruby, Swift, Kotlin, Dart
//! **Scripting**: Lua, R, Bash, PowerShell
//! **Specialized**: GDScript, Razor, SQL, Regex
//! **Documentation**: Markdown, JSON, TOML, YAML

// Core infrastructure
pub mod base;
pub mod factory;
pub mod language;
pub mod manager;
pub mod utils;

// Language extractors (31 total)
pub mod bash;
pub mod c;
pub mod cpp;
pub mod csharp;
pub mod css;
pub mod dart;
pub mod gdscript;
pub mod go;
pub mod html;
pub mod java;
pub mod javascript;
pub mod json;
pub mod kotlin;
pub mod lua;
pub mod markdown;
pub mod php;
pub mod powershell;
pub mod python;
pub mod qml;
pub mod r;
pub mod razor;
pub mod regex;
pub mod ruby;
pub mod rust;
pub mod sql;
pub mod swift;
pub mod toml;
pub mod typescript;
pub mod vue;
pub mod yaml;
pub mod zig;

// Re-export the public API - Core types
pub use base::{
    BaseExtractor, ContextConfig, ExtractionResults, Identifier, IdentifierKind,
    PendingRelationship, Relationship, RelationshipKind, Symbol, SymbolKind, SymbolOptions,
    TypeInfo, Visibility,
};

// Re-export the public API - Extraction functions
pub use language::{detect_language, get_tree_sitter_language};
pub use manager::ExtractorManager;

// Re-export extractors
pub use bash::BashExtractor;
pub use c::CExtractor;
pub use cpp::CppExtractor;
pub use csharp::CSharpExtractor;
pub use css::CSSExtractor;
pub use dart::DartExtractor;
pub use gdscript::GDScriptExtractor;
pub use go::GoExtractor;
pub use html::HTMLExtractor;
pub use java::JavaExtractor;
pub use javascript::JavaScriptExtractor;
pub use json::JsonExtractor;
pub use kotlin::KotlinExtractor;
pub use lua::LuaExtractor;
pub use markdown::MarkdownExtractor;
pub use php::PhpExtractor;
pub use powershell::PowerShellExtractor;
pub use python::PythonExtractor;
pub use qml::QmlExtractor;
pub use r::RExtractor;
pub use razor::RazorExtractor;
pub use regex::RegexExtractor;
pub use ruby::RubyExtractor;
pub use rust::RustExtractor;
pub use sql::SqlExtractor;
pub use swift::SwiftExtractor;
pub use toml::TomlExtractor;
pub use typescript::TypeScriptExtractor;
pub use vue::VueExtractor;
pub use yaml::YamlExtractor;
pub use zig::ZigExtractor;

#[cfg(test)]
mod tests;
