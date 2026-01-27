//! TypeScript/JavaScript symbol extractor with modular architecture
//!
//! This module provides comprehensive symbol extraction for TypeScript, JavaScript, and TSX/JSX files.
//! The architecture is organized into specialized modules for clarity and maintainability:
//!
//! - **symbols**: Core symbol extraction logic for classes, functions, interfaces, etc.
//! - **functions**: Function and method extraction with signature building
//! - **classes**: Class extraction with inheritance and modifiers
//! - **interfaces**: Interface and type alias extraction
//! - **imports_exports**: Import/export statement extraction
//! - **relationships**: Function call and inheritance relationship tracking
//! - **inference**: Type inference from assignments and return statements
//! - **identifiers**: Identifier usage extraction (calls, member access, etc.)
//! - **helpers**: Utility functions for tree traversal and text extraction

mod classes;
mod functions;
mod helpers;
mod identifiers;
mod imports_exports;
pub mod inference;
mod interfaces;
pub(crate) mod relationships;
mod symbols;

use crate::base::{BaseExtractor, Identifier, PendingRelationship, Relationship, RelationshipKind, Symbol, SymbolKind};
use std::collections::HashMap;
use tree_sitter::Tree;

/// Main TypeScript extractor that orchestrates modular extraction components
pub struct TypeScriptExtractor {
    base: BaseExtractor,
    /// Pending relationships that need cross-file resolution after workspace indexing
    pending_relationships: Vec<PendingRelationship>,
}

impl TypeScriptExtractor {
    /// Create a new TypeScript extractor
    ///
    /// # Phase 2: Relative Unix-Style Path Storage
    /// Now accepts workspace_root to enable relative path storage
    pub fn new(
        language: String,
        file_path: String,
        content: String,
        workspace_root: &std::path::Path,
    ) -> Self {
        Self {
            base: BaseExtractor::new(language, file_path, content, workspace_root),
            pending_relationships: Vec::new(),
        }
    }

    /// Extract all symbols from the syntax tree
    pub fn extract_symbols(&mut self, tree: &Tree) -> Vec<Symbol> {
        symbols::extract_symbols(self, tree)
    }

    /// Extract all relationships (calls, inheritance, etc.)
    pub fn extract_relationships(&mut self, tree: &Tree, symbols: &[Symbol]) -> Vec<Relationship> {
        let rels = relationships::extract_relationships(self, tree, symbols);
        // Extract pending relationships (cross-file calls) and add them to our internal list
        self.extract_pending_relationships(tree, symbols);
        rels
    }

    /// Extract pending relationships from the syntax tree
    /// This handles cross-file function calls that need resolution
    fn extract_pending_relationships(&mut self, tree: &Tree, symbols: &[Symbol]) {
        let symbol_map: std::collections::HashMap<String, &Symbol> =
            symbols.iter().map(|s| (s.name.clone(), s)).collect();

        self.walk_for_pending_calls(tree.root_node(), &symbol_map);
    }

    /// Walk the tree looking for function calls that reference imported symbols
    fn walk_for_pending_calls(&mut self, node: tree_sitter::Node, symbol_map: &std::collections::HashMap<String, &Symbol>) {
        // Look for call expressions
        if node.kind() == "call_expression" {
            if let Some(function_node) = node.child_by_field_name("function") {
                // Extract function name - handle both direct calls and member access
                let function_name = if function_node.kind() == "member_expression" {
                    // For obj.method(), get just "method"
                    if let Some(property) = function_node.child_by_field_name("property") {
                        self.base.get_node_text(&property)
                    } else {
                        self.base.get_node_text(&function_node)
                    }
                } else {
                    self.base.get_node_text(&function_node)
                };

                // Check if this is a call to an import or unknown function
                match symbol_map.get(function_name.as_str()) {
                    Some(called_symbol) if called_symbol.kind == SymbolKind::Import => {
                        // This is a call to an imported function - create pending relationship
                        // Find the containing function
                        if let Some(caller_symbol) = self.find_containing_function_in_symbols(node, symbol_map) {
                            let line_number = node.start_position().row as u32 + 1;
                            self.add_pending_relationship(PendingRelationship {
                                from_symbol_id: caller_symbol.id.clone(),
                                callee_name: function_name.clone(),
                                kind: RelationshipKind::Calls,
                                file_path: self.base.file_path.clone(),
                                line_number,
                                confidence: 0.8,
                            });
                        }
                    }
                    None => {
                        // Unknown function - could be from another file
                        // Check if it's being called from within a function
                        if let Some(caller_symbol) = self.find_containing_function_in_symbols(node, symbol_map) {
                            let line_number = node.start_position().row as u32 + 1;
                            self.add_pending_relationship(PendingRelationship {
                                from_symbol_id: caller_symbol.id.clone(),
                                callee_name: function_name.clone(),
                                kind: RelationshipKind::Calls,
                                file_path: self.base.file_path.clone(),
                                line_number,
                                confidence: 0.7,
                            });
                        }
                    }
                    _ => {}
                }
            }
        }

        // Recursively process children
        let mut cursor = node.walk();
        for child in node.children(&mut cursor) {
            self.walk_for_pending_calls(child, symbol_map);
        }
    }

    /// Find the containing function for a node by walking up the tree
    fn find_containing_function_in_symbols<'a>(
        &self,
        node: tree_sitter::Node,
        symbol_map: &'a std::collections::HashMap<String, &'a Symbol>,
    ) -> Option<&'a Symbol> {
        let mut current = node.parent();

        while let Some(current_node) = current {
            // Check for function declarations
            if current_node.kind() == "function_declaration"
                || current_node.kind() == "method_definition"
                || current_node.kind() == "arrow_function"
            {
                // Get the function name
                if let Some(name_node) = current_node.child_by_field_name("name") {
                    let func_name = self.base.get_node_text(&name_node);
                    if let Some(symbol) = symbol_map.get(&func_name) {
                        if matches!(symbol.kind, SymbolKind::Function | SymbolKind::Method) {
                            return Some(symbol);
                        }
                    }
                }
            }

            current = current_node.parent();
        }

        None
    }

    /// Extract all identifiers (function calls, member access, etc.)
    pub fn extract_identifiers(&mut self, tree: &Tree, symbols: &[Symbol]) -> Vec<Identifier> {
        identifiers::extract_identifiers(self, tree, symbols)
    }

    /// Infer types from variable assignments and function returns
    pub fn infer_types(&self, symbols: &[Symbol]) -> HashMap<String, String> {
        inference::infer_types(self, symbols)
    }

    /// Get pending relationships that need cross-file resolution
    pub fn get_pending_relationships(&self) -> Vec<PendingRelationship> {
        self.pending_relationships.clone()
    }

    /// Add a pending relationship (used during extraction)
    pub fn add_pending_relationship(&mut self, pending: PendingRelationship) {
        self.pending_relationships.push(pending);
    }

    // ========================================================================
    // Public access to base for sub-modules (pub(super) scoped internal access)
    // ========================================================================

    /// Get mutable reference to base extractor (for sub-modules)
    pub(crate) fn base_mut(&mut self) -> &mut BaseExtractor {
        &mut self.base
    }

    /// Get immutable reference to base extractor (for sub-modules)
    pub(crate) fn base(&self) -> &BaseExtractor {
        &self.base
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    fn parse_typescript(content: &str) -> tree_sitter::Tree {
        let mut parser = tree_sitter::Parser::new();
        parser
            .set_language(&tree_sitter_typescript::LANGUAGE_TYPESCRIPT.into())
            .expect("Failed to set TypeScript language");
        parser.parse(content, None).expect("Failed to parse")
    }

    #[test]
    fn test_extract_function() {
        let content = r#"
function greet(name: string): string {
    return "Hello, " + name;
}
"#;
        let tree = parse_typescript(content);
        let workspace_root = Path::new("/test");
        let mut extractor = TypeScriptExtractor::new(
            "typescript".to_string(),
            "test.ts".to_string(),
            content.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);

        assert!(!symbols.is_empty(), "Should extract at least one symbol");

        let func = symbols.iter().find(|s| s.name == "greet");
        assert!(func.is_some(), "Should find 'greet' function");

        let func = func.unwrap();
        assert_eq!(func.kind, SymbolKind::Function);
        assert!(func.signature.as_ref().map_or(false, |s| s.contains("greet")));
    }

    #[test]
    fn test_extract_class_with_method() {
        let content = r#"
class Calculator {
    add(a: number, b: number): number {
        return a + b;
    }
}
"#;
        let tree = parse_typescript(content);
        let workspace_root = Path::new("/test");
        let mut extractor = TypeScriptExtractor::new(
            "typescript".to_string(),
            "test.ts".to_string(),
            content.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);

        let class = symbols.iter().find(|s| s.name == "Calculator");
        assert!(class.is_some(), "Should find 'Calculator' class");
        assert_eq!(class.unwrap().kind, SymbolKind::Class);

        let method = symbols.iter().find(|s| s.name == "add");
        assert!(method.is_some(), "Should find 'add' method");
        assert_eq!(method.unwrap().kind, SymbolKind::Method);
    }

    #[test]
    fn test_extract_interface() {
        let content = r#"
interface User {
    name: string;
    age: number;
}
"#;
        let tree = parse_typescript(content);
        let workspace_root = Path::new("/test");
        let mut extractor = TypeScriptExtractor::new(
            "typescript".to_string(),
            "test.ts".to_string(),
            content.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);

        let interface = symbols.iter().find(|s| s.name == "User");
        assert!(interface.is_some(), "Should find 'User' interface");
        assert_eq!(interface.unwrap().kind, SymbolKind::Interface);
    }

    #[test]
    fn test_extract_arrow_function() {
        let content = r#"
const multiply = (a: number, b: number): number => a * b;
"#;
        let tree = parse_typescript(content);
        let workspace_root = Path::new("/test");
        let mut extractor = TypeScriptExtractor::new(
            "typescript".to_string(),
            "test.ts".to_string(),
            content.to_string(),
            workspace_root,
        );

        let symbols = extractor.extract_symbols(&tree);

        let func = symbols.iter().find(|s| s.name == "multiply");
        assert!(func.is_some(), "Should find 'multiply' arrow function");
        assert_eq!(func.unwrap().kind, SymbolKind::Function);
    }
}
