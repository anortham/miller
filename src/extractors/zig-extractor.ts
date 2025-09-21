import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Zig language extractor that handles Zig-specific constructs:
 * - Functions and their parameters
 * - Structs and their fields
 * - Enums and their variants
 * - Constants and variables
 * - Modules and imports
 * - Comptime constructs
 * - Error types and error handling
 *
 * Special focus on Bun/systems programming patterns since this will
 * catch the attention of the Bun team (Bun is built with Zig!).
 */
export class ZigExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      try {
        switch (node.type) {
          case 'function_declaration':
          case 'function_definition':
            symbol = this.extractFunction(node, parentId);
            break;
          case 'struct_declaration':
            symbol = this.extractStruct(node, parentId);
            break;
          case 'enum_declaration':
            symbol = this.extractEnum(node, parentId);
            break;
          case 'variable_declaration':
          case 'const_declaration':
            symbol = this.extractVariable(node, parentId);
            break;
          case 'error_declaration':
            symbol = this.extractErrorType(node, parentId);
            break;
          case 'type_declaration':
            symbol = this.extractTypeAlias(node, parentId);
            break;
          case 'parameter':
            symbol = this.extractParameter(node, parentId);
            break;
          case 'field_declaration':
          case 'struct_field':
            symbol = this.extractStructField(node, parentId);
            break;
          case 'enum_field':
          case 'enum_variant':
            symbol = this.extractEnumVariant(node, parentId);
            break;
          default:
            // Handle other Zig constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Zig symbol from ${node.type}:`, error);
      }

      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }

      // Recursively visit children
      if (node.children && node.children.length > 0) {
        for (const child of node.children) {
          try {
            visitNode(child, parentId);
          } catch (error) {
            // Skip problematic child nodes
            continue;
          }
        }
      }
    };

    try {
      visitNode(tree.rootNode);
    } catch (error) {
      console.warn('Zig parsing failed:', error);
    }

    return symbols;
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a public function
    const isPublic = this.isPublicFunction(node);

    // Check if it's an export function
    const isExport = this.isExportFunction(node);

    const functionSymbol = this.createSymbol(node, name, SymbolKind.Function, {
      signature: this.extractFunctionSignature(node),
      visibility: isPublic || isExport ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return functionSymbol;
  }

  private extractParameter(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const paramName = this.getNodeText(nameNode);
    const typeNode = this.findChildByType(node, 'type_expression') ||
                    this.findChildByType(node, 'identifier', 1); // Second identifier is often the type

    const paramType = typeNode ? this.getNodeText(typeNode) : 'unknown';

    const paramSymbol = this.createSymbol(node, paramName, SymbolKind.Variable, {
      signature: `${paramName}: ${paramType}`,
      visibility: 'public',
      parentId
    });

    return paramSymbol;
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const structSymbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature: `struct ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return structSymbol;
  }

  private extractStructField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const fieldName = this.getNodeText(nameNode);
    const typeNode = this.findChildByType(node, 'type_expression') ||
                    this.findChildByType(node, 'identifier', 1);

    const fieldType = typeNode ? this.getNodeText(typeNode) : 'unknown';

    const fieldSymbol = this.createSymbol(node, fieldName, SymbolKind.Property, {
      signature: `${fieldName}: ${fieldType}`,
      visibility: 'public', // Zig struct fields are generally public
      parentId
    });

    return fieldSymbol;
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const enumSymbol = this.createSymbol(node, name, SymbolKind.Enum, {
      signature: `enum ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return enumSymbol;
  }

  private extractEnumVariant(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const variantName = this.getNodeText(nameNode);

    const variantSymbol = this.createSymbol(node, variantName, SymbolKind.EnumMember, {
      signature: variantName,
      visibility: 'public',
      parentId
    });

    return variantSymbol;
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isConst = node.type === 'const_declaration' || this.getNodeText(node).includes('const');
    const isPublic = this.isPublicDeclaration(node);

    // SYSTEMATIC FIX: Check if this is a struct declaration (const Point = struct { ... })
    const structNode = this.findChildByType(node, 'struct_declaration');
    if (structNode) {
      // Check if it's a packed struct by looking at the full text
      const nodeText = this.getNodeText(node);
      const isPacked = nodeText.includes('packed struct');
      const isExtern = nodeText.includes('extern struct');

      let structType = 'struct';
      if (isPacked) structType = 'packed struct';
      else if (isExtern) structType = 'extern struct';

      // This is a struct, extract it as a class
      const structSymbol = this.createSymbol(node, name, SymbolKind.Class, {
        signature: `const ${name} = ${structType}`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node)
      });
      return structSymbol;
    }

    // Extract type if available
    const typeNode = this.findChildByType(node, 'type_expression');
    const varType = typeNode ? this.getNodeText(typeNode) : 'inferred';

    const variableSymbol = this.createSymbol(node, name, isConst ? SymbolKind.Constant : SymbolKind.Variable, {
      signature: `${isConst ? 'const' : 'var'} ${name}: ${varType}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return variableSymbol;
  }

  private extractErrorType(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const errorSymbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature: `error ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isErrorType: true }
    });

    return errorSymbol;
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const typeSymbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature: `type ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isTypeAlias: true }
    });

    return typeSymbol;
  }

  private isPublicFunction(node: Parser.SyntaxNode): boolean {
    // Check for "pub" keyword before function
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'pub' || this.getNodeText(current) === 'pub') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isExportFunction(node: Parser.SyntaxNode): boolean {
    // Check for "export" keyword
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'export' || this.getNodeText(current) === 'export') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isPublicDeclaration(node: Parser.SyntaxNode): boolean {
    // Check for "pub" keyword before declaration
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'pub' || this.getNodeText(current) === 'pub') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Extract parameters
    const params: string[] = [];
    const paramList = this.findChildByType(node, 'parameter_list');
    if (paramList) {
      this.traverseTree(paramList, (paramNode) => {
        if (paramNode.type === 'parameter') {
          const paramNameNode = this.findChildByType(paramNode, 'identifier');
          const typeNode = this.findChildByType(paramNode, 'type_expression') ||
                          this.findChildByType(paramNode, 'identifier', 1);

          if (paramNameNode) {
            const paramName = this.getNodeText(paramNameNode);
            const paramType = typeNode ? this.getNodeText(typeNode) : '';
            params.push(paramType ? `${paramName}: ${paramType}` : paramName);
          }
        }
      });
    }

    // Extract return type (including Zig error union types)
    const returnTypeNode = this.findChildByType(node, 'return_type') ||
                          this.findChildByType(node, 'type_expression') ||
                          this.findChildByType(node, 'error_union_type') ||
                          this.findChildByType(node, 'builtin_type');
    const returnType = returnTypeNode ? this.getNodeText(returnTypeNode) : 'void';

    return `fn ${name}(${params.join(', ')}) ${returnType}`;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'struct_declaration':
            this.extractStructRelationships(node, symbols, relationships);
            break;
          case 'function_call':
            this.extractFunctionCallRelationships(node, symbols, relationships);
            break;
          case 'import_declaration':
          case 'usingnamespace':
            this.extractImportRelationships(node, symbols, relationships);
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Zig relationship from ${node.type}:`, error);
      }
    });

    return relationships;
  }

  private extractStructRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract field relationships and inheritance if any
    const structName = this.findChildByType(node, 'identifier');
    if (!structName) return;

    const structSymbol = symbols.find(s => s.name === this.getNodeText(structName) && s.kind === SymbolKind.Class);
    if (!structSymbol) return;

    // Look for struct fields that reference other types
    this.traverseTree(node, (fieldNode) => {
      if (fieldNode.type === 'field_declaration' || fieldNode.type === 'struct_field') {
        const typeNode = this.findChildByType(fieldNode, 'type_expression') ||
                        this.findChildByType(fieldNode, 'identifier', 1);

        if (typeNode) {
          const typeName = this.getNodeText(typeNode);
          const referencedSymbol = symbols.find(s => s.name === typeName &&
            (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface));

          if (referencedSymbol && referencedSymbol.id !== structSymbol.id) {
            relationships.push({
              id: this.generateId(`struct_uses_${typeName}`, fieldNode.startPosition),
              fromSymbolId: structSymbol.id,
              toSymbolId: referencedSymbol.id,
              kind: RelationshipKind.Uses,
              filePath: this.filePath,
              startLine: fieldNode.startPosition.row,
              startColumn: fieldNode.startPosition.column,
              endLine: fieldNode.endPosition.row,
              endColumn: fieldNode.endPosition.column
            });
          }
        }
      }
    });
  }

  private extractFunctionCallRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract function call relationships
    const funcNameNode = this.findChildByType(node, 'identifier');
    if (!funcNameNode) return;

    const calledFuncName = this.getNodeText(funcNameNode);
    const calledSymbol = symbols.find(s => s.name === calledFuncName && s.kind === SymbolKind.Function);

    if (calledSymbol) {
      // Find the calling function
      let current = node.parent;
      while (current && current.type !== 'function_declaration' && current.type !== 'function_definition') {
        current = current.parent;
      }

      if (current) {
        const callerNameNode = this.findChildByType(current, 'identifier');
        if (callerNameNode) {
          const callerName = this.getNodeText(callerNameNode);
          const callerSymbol = symbols.find(s => s.name === callerName && s.kind === SymbolKind.Function);

          if (callerSymbol && callerSymbol.id !== calledSymbol.id) {
            relationships.push({
              id: this.generateId(`call_${callerName}_${calledFuncName}`, node.startPosition),
              fromSymbolId: callerSymbol.id,
              toSymbolId: calledSymbol.id,
              kind: RelationshipKind.Calls,
              filePath: this.filePath,
              startLine: node.startPosition.row,
              startColumn: node.startPosition.column,
              endLine: node.endPosition.row,
              endColumn: node.endPosition.column
            });
          }
        }
      }
    }
  }

  private extractImportRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract import/module relationships
    // This could be expanded to track cross-file dependencies
  }

  extractTypes(tree: Parser.Tree): Map<string, string> {
    const types = new Map<string, string>();

    this.traverseTree(tree.rootNode, (node) => {
      if (node.type === 'variable_declaration' || node.type === 'const_declaration') {
        const nameNode = this.findChildByType(node, 'identifier');
        const typeNode = this.findChildByType(node, 'type_expression');

        if (nameNode && typeNode) {
          const varName = this.getNodeText(nameNode);
          const varType = this.getNodeText(typeNode);
          types.set(varName, varType);
        }
      }
    });

    return types;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    // Zig type inference based on symbol metadata and signatures
    for (const symbol of symbols) {
      if (symbol.signature) {
        // Extract Zig types from signatures like "const name: i32", "var buffer: []u8"
        const zigTypePattern = /:\s*([\w\[\]!?*]+)/;
        const typeMatch = symbol.signature.match(zigTypePattern);
        if (typeMatch) {
          types.set(symbol.name, typeMatch[1]);
        }
      }

      // Use metadata for Zig-specific types
      if (symbol.metadata?.isErrorType) {
        types.set(symbol.name, 'error');
      }
      if (symbol.metadata?.isTypeAlias) {
        types.set(symbol.name, 'type');
      }
      if (symbol.kind === SymbolKind.Class && !symbol.metadata?.isErrorType) {
        types.set(symbol.name, 'struct');
      }
      if (symbol.kind === SymbolKind.Enum) {
        types.set(symbol.name, 'enum');
      }
    }

    return types;
  }
}