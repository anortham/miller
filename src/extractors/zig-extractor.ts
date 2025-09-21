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

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'function_declaration':
          case 'function_definition':
            this.extractFunction(node, symbols);
            break;
          case 'struct_declaration':
            this.extractStruct(node, symbols);
            break;
          case 'enum_declaration':
            this.extractEnum(node, symbols);
            break;
          case 'variable_declaration':
          case 'const_declaration':
            this.extractVariable(node, symbols);
            break;
          case 'error_declaration':
            this.extractErrorType(node, symbols);
            break;
          case 'type_declaration':
            this.extractTypeAlias(node, symbols);
            break;
          default:
            // Handle other Zig constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Zig symbol from ${node.type}:`, error);
      }
    });

    return symbols;
  }

  private extractFunction(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);

    // Check if it's a public function
    const isPublic = this.isPublicFunction(node);

    // Check if it's an export function
    const isExport = this.isExportFunction(node);

    const functionSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Function,
      signature: this.extractFunctionSignature(node),
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: isPublic || isExport ? 'public' : 'private',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(functionSymbol);

    // Extract function parameters
    this.extractFunctionParameters(node, symbols, functionSymbol.id);
  }

  private extractFunctionParameters(funcNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    const paramList = this.findChildByType(funcNode, 'parameter_list');
    if (!paramList) return;

    this.traverseTree(paramList, (node) => {
      if (node.type === 'parameter') {
        const nameNode = this.findChildByType(node, 'identifier');
        if (!nameNode) return;

        const paramName = this.getNodeText(nameNode);
        const typeNode = this.findChildByType(node, 'type_expression') ||
                        this.findChildByType(node, 'identifier', 1); // Second identifier is often the type

        const paramType = typeNode ? this.getNodeText(typeNode) : 'unknown';

        const paramSymbol: Symbol = {
          id: this.generateId(`${paramName}_param`, node.startPosition),
          name: paramName,
          kind: SymbolKind.Variable,
          signature: `${paramName}: ${paramType}`,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public'
        };

        symbols.push(paramSymbol);
      }
    });
  }

  private extractStruct(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const structSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Class, // Structs are like classes in Zig
      signature: `struct ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: isPublic ? 'public' : 'private',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(structSymbol);

    // Extract struct fields
    this.extractStructFields(node, symbols, structSymbol.id);
  }

  private extractStructFields(structNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(structNode, (node) => {
      if (node.type === 'field_declaration' || node.type === 'struct_field') {
        const nameNode = this.findChildByType(node, 'identifier');
        if (!nameNode) return;

        const fieldName = this.getNodeText(nameNode);
        const typeNode = this.findChildByType(node, 'type_expression') ||
                        this.findChildByType(node, 'identifier', 1);

        const fieldType = typeNode ? this.getNodeText(typeNode) : 'unknown';

        const fieldSymbol: Symbol = {
          id: this.generateId(`${fieldName}_field`, node.startPosition),
          name: fieldName,
          kind: SymbolKind.Property,
          signature: `${fieldName}: ${fieldType}`,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public' // Zig struct fields are generally public
        };

        symbols.push(fieldSymbol);
      }
    });
  }

  private extractEnum(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const enumSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Enum,
      signature: `enum ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: isPublic ? 'public' : 'private',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(enumSymbol);

    // Extract enum variants
    this.extractEnumVariants(node, symbols, enumSymbol.id);
  }

  private extractEnumVariants(enumNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(enumNode, (node) => {
      if (node.type === 'enum_field' || node.type === 'enum_variant') {
        const nameNode = this.findChildByType(node, 'identifier');
        if (!nameNode) return;

        const variantName = this.getNodeText(nameNode);

        const variantSymbol: Symbol = {
          id: this.generateId(`${variantName}_variant`, node.startPosition),
          name: variantName,
          kind: SymbolKind.EnumMember,
          signature: variantName,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public'
        };

        symbols.push(variantSymbol);
      }
    });
  }

  private extractVariable(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isConst = node.type === 'const_declaration' || this.getNodeText(node).includes('const');
    const isPublic = this.isPublicDeclaration(node);

    // Extract type if available
    const typeNode = this.findChildByType(node, 'type_expression');
    const varType = typeNode ? this.getNodeText(typeNode) : 'inferred';

    const variableSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: isConst ? SymbolKind.Constant : SymbolKind.Variable,
      signature: `${isConst ? 'const' : 'var'} ${name}: ${varType}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: isPublic ? 'public' : 'private',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(variableSymbol);
  }

  private extractErrorType(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);

    const errorSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Class, // Error types are like special classes
      signature: `error ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: 'public',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(errorSymbol);
  }

  private extractTypeAlias(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const typeSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Interface, // Type aliases are like interfaces
      signature: `type ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: isPublic ? 'public' : 'private',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(typeSymbol);
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

    // Extract return type
    const returnTypeNode = this.findChildByType(node, 'return_type') ||
                          this.findChildByType(node, 'type_expression');
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
              sourceId: structSymbol.id,
              targetId: referencedSymbol.id,
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
              sourceId: callerSymbol.id,
              targetId: calledSymbol.id,
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
}