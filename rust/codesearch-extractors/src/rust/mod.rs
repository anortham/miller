/// Rust language extractor with support for:
/// - Structs, enums, traits, unions
/// - Functions, methods, impl blocks
/// - Modules, macros, type aliases
/// - Constants, statics
/// - Two-phase processing: extract symbols → process impl blocks
///
/// Implementation of comprehensive Rust extractor
use crate::base::{
    BaseExtractor, Identifier, PendingRelationship, Relationship, Symbol, SymbolKind,
};
use once_cell::sync::Lazy;
use regex::Regex;
use tree_sitter::{Node, Tree};

// Static regexes for type inference (compiled once, reused across all calls)
static RETURN_TYPE_RE: Lazy<Regex> = Lazy::new(|| {
    Regex::new(r"->\s*([^{]+)").unwrap()
});
static TYPE_ANNOTATION_RE: Lazy<Regex> = Lazy::new(|| {
    Regex::new(r":\s*([^=\s{]+)").unwrap()
});

// Private modules
mod functions;
mod helpers;
mod identifiers;
mod relationships;
mod signatures;
mod types;

// Re-export types
pub use self::helpers::ImplBlockInfo;

// Use helpers in the orchestrator
use self::helpers::is_inside_impl;

/// Rust extractor that handles Rust-specific constructs
pub struct RustExtractor {
    base: BaseExtractor,
    impl_blocks: Vec<ImplBlockInfo>,
    is_processing_impl_blocks: bool,
    /// Pending relationships that need cross-file resolution after workspace indexing
    pending_relationships: Vec<PendingRelationship>,
}

impl RustExtractor {
    pub fn new(
        language: String,
        file_path: String,
        content: String,
        workspace_root: &std::path::Path,
    ) -> Self {
        Self {
            base: BaseExtractor::new(language, file_path, content, workspace_root),
            impl_blocks: Vec::new(),
            is_processing_impl_blocks: false,
            pending_relationships: Vec::new(),
        }
    }

    /// Get pending relationships that need cross-file resolution
    pub fn get_pending_relationships(&self) -> Vec<PendingRelationship> {
        self.pending_relationships.clone()
    }

    /// Add a pending relationship (used during extraction)
    pub fn add_pending_relationship(&mut self, pending: PendingRelationship) {
        self.pending_relationships.push(pending);
    }

    /// Extract symbols using two-phase approach
    /// Phase 1: Extract all symbols except methods in impl blocks
    /// Phase 2: Process impl blocks and link methods to parent structs/traits
    pub fn extract_symbols(&mut self, tree: &Tree) -> Vec<Symbol> {
        let mut symbols = Vec::new();

        // Phase 1: Extract symbols (skip impl block methods)
        self.impl_blocks.clear();
        self.is_processing_impl_blocks = false;
        self.walk_tree(tree.root_node(), &mut symbols, None);

        // Phase 2: Process impl blocks after all symbols are extracted
        // SAFETY FIX: Pass tree reference so we can reconstruct nodes from byte ranges
        self.is_processing_impl_blocks = true;
        self.process_impl_blocks(tree, &mut symbols);

        symbols
    }

    fn walk_tree(&mut self, node: Node, symbols: &mut Vec<Symbol>, parent_id: Option<String>) {
        if let Some(symbol) = self.extract_symbol(node, parent_id.clone()) {
            let symbol_id = symbol.id.clone();
            symbols.push(symbol);

            // Continue traversing with new parent_id for nested symbols
            let mut cursor = node.walk();
            for child in node.children(&mut cursor) {
                self.walk_tree(child, symbols, Some(symbol_id.clone()));
            }
        } else {
            // No symbol extracted, continue with current parent_id
            let mut cursor = node.walk();
            for child in node.children(&mut cursor) {
                self.walk_tree(child, symbols, parent_id.clone());
            }
        }
    }

    fn extract_symbol(&mut self, node: Node, parent_id: Option<String>) -> Option<Symbol> {
        match node.kind() {
            "struct_item" => Some(types::extract_struct(self, node, parent_id)),
            "enum_item" => Some(types::extract_enum(self, node, parent_id)),
            "trait_item" => Some(types::extract_trait(self, node, parent_id)),
            "impl_item" => {
                functions::extract_impl(self, node, parent_id);
                None // impl blocks don't create symbols directly
            }
            "function_item" => {
                // Skip if inside impl block during phase 1
                if is_inside_impl(node) && !self.is_processing_impl_blocks {
                    None
                } else {
                    Some(functions::extract_function(self, node, parent_id))
                }
            }
            "function_signature_item" => Some(signatures::extract_function_signature(
                self, node, parent_id,
            )),
            "associated_type" => Some(signatures::extract_associated_type(self, node, parent_id)),
            "union_item" => Some(types::extract_union(self, node, parent_id)),
            "macro_invocation" => signatures::extract_macro_invocation(self, node, parent_id),
            "mod_item" => Some(types::extract_module(self, node, parent_id)),
            "use_declaration" => signatures::extract_use(self, node, parent_id),
            "const_item" => Some(types::extract_const(self, node, parent_id)),
            "static_item" => Some(types::extract_static(self, node, parent_id)),
            "macro_definition" => Some(types::extract_macro(self, node, parent_id)),
            "type_item" => Some(types::extract_type_alias(self, node, parent_id)),
            _ => None,
        }
    }

    fn process_impl_blocks(&mut self, tree: &Tree, symbols: &mut Vec<Symbol>) {
        functions::process_impl_blocks(self, tree, symbols);
    }

    pub fn extract_relationships(&mut self, tree: &Tree, symbols: &[Symbol]) -> Vec<Relationship> {
        relationships::extract_relationships(self, tree, symbols)
    }

    pub fn extract_identifiers(&mut self, tree: &Tree, symbols: &[Symbol]) -> Vec<Identifier> {
        identifiers::extract_identifiers(self, tree, symbols)
    }

    /// Infer types from Rust signatures (function return types, variable types, field types)
    pub fn infer_types(&self, symbols: &[Symbol]) -> std::collections::HashMap<String, String> {
        let mut type_map = std::collections::HashMap::new();

        for symbol in symbols {
            // For functions/methods, try to extract return type from signature
            if matches!(symbol.kind, SymbolKind::Function | SymbolKind::Method) {
                if let Some(ref signature) = symbol.signature {
                    // Extract return type using regex: "-> Type"
                    if let Some(captures) = RETURN_TYPE_RE.captures(signature) {
                        let return_type = captures[1].trim().to_string();
                        if !return_type.is_empty() {
                            type_map.insert(symbol.id.clone(), return_type);
                        }
                    }
                }
            }
            // For variables, properties, fields - extract type annotation
            else if matches!(
                symbol.kind,
                SymbolKind::Variable | SymbolKind::Property | SymbolKind::Field
            ) {
                if let Some(ref signature) = symbol.signature {
                    // Extract type from annotations: "name: Type" or "name: Type ="
                    if let Some(captures) = TYPE_ANNOTATION_RE.captures(signature) {
                        let type_str = captures[1].trim().to_string();
                        if !type_str.is_empty() {
                            type_map.insert(symbol.id.clone(), type_str);
                        }
                    }
                }
            }
        }

        type_map
    }

    // Accessors for use by submodules and tests
    pub(crate) fn get_base_mut(&mut self) -> &mut BaseExtractor {
        &mut self.base
    }

    pub(super) fn get_impl_blocks(&self) -> &[ImplBlockInfo] {
        &self.impl_blocks
    }

    pub(super) fn add_impl_block(&mut self, block: ImplBlockInfo) {
        self.impl_blocks.push(block);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::language::get_tree_sitter_language;
    use std::path::Path;

    fn parse_rust(code: &str) -> Tree {
        let mut parser = tree_sitter::Parser::new();
        parser
            .set_language(&get_tree_sitter_language("rust").unwrap())
            .unwrap();
        parser.parse(code, None).unwrap()
    }

    #[test]
    fn test_extract_rust_function() {
        let code = "pub fn hello() {}";
        let tree = parse_rust(code);
        let workspace_root = Path::new("/test");

        let mut extractor = RustExtractor::new(
            "rust".to_string(),
            "/test/main.rs".to_string(),
            code.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);
        assert!(symbols.iter().any(|s| s.name == "hello"));

        let hello = symbols.iter().find(|s| s.name == "hello").unwrap();
        assert_eq!(hello.kind, SymbolKind::Function);
    }

    #[test]
    fn test_extract_rust_struct() {
        let code = "pub struct MyStruct { field: i32 }";
        let tree = parse_rust(code);
        let workspace_root = Path::new("/test");

        let mut extractor = RustExtractor::new(
            "rust".to_string(),
            "/test/main.rs".to_string(),
            code.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);
        assert!(symbols.iter().any(|s| s.name == "MyStruct"));

        let my_struct = symbols.iter().find(|s| s.name == "MyStruct").unwrap();
        assert_eq!(my_struct.kind, SymbolKind::Class);
    }

    #[test]
    fn test_extract_rust_impl_method() {
        let code = r#"
struct Foo {}

impl Foo {
    pub fn bar(&self) {}
}
"#;
        let tree = parse_rust(code);
        let workspace_root = Path::new("/test");

        let mut extractor = RustExtractor::new(
            "rust".to_string(),
            "/test/main.rs".to_string(),
            code.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);

        // Should have struct and method
        assert!(symbols.iter().any(|s| s.name == "Foo"));
        assert!(symbols.iter().any(|s| s.name == "bar"));

        let bar = symbols.iter().find(|s| s.name == "bar").unwrap();
        assert_eq!(bar.kind, SymbolKind::Method);
    }

    #[test]
    fn test_extract_rust_trait() {
        let code = "pub trait MyTrait { fn required(&self); }";
        let tree = parse_rust(code);
        let workspace_root = Path::new("/test");

        let mut extractor = RustExtractor::new(
            "rust".to_string(),
            "/test/main.rs".to_string(),
            code.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);
        assert!(symbols.iter().any(|s| s.name == "MyTrait"));

        let my_trait = symbols.iter().find(|s| s.name == "MyTrait").unwrap();
        assert_eq!(my_trait.kind, SymbolKind::Interface);
    }
}
