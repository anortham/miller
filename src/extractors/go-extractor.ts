import Parser from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Go language extractor that handles Go-specific constructs including:
 * - Structs, interfaces, and type aliases
 * - Functions and methods with receivers
 * - Packages and imports
 * - Constants and variables
 * - Goroutines and channels
 * - Interface implementations and embedding
 */
export class GoExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.walkTree(tree.rootNode, symbols);

    // Prioritize functions over fields with the same name
    return this.prioritizeFunctionsOverFields(symbols);
  }

  private prioritizeFunctionsOverFields(symbols: Symbol[]): Symbol[] {
    const symbolMap = new Map<string, Symbol[]>();

    // Group symbols by name
    for (const symbol of symbols) {
      if (!symbolMap.has(symbol.name)) {
        symbolMap.set(symbol.name, []);
      }
      symbolMap.get(symbol.name)!.push(symbol);
    }

    const result: Symbol[] = [];

    // For each name group, add functions first, then other types
    for (const [name, symbolGroup] of symbolMap) {
      const functions = symbolGroup.filter(s => s.kind === SymbolKind.Function || s.kind === SymbolKind.Method);
      const others = symbolGroup.filter(s => s.kind !== SymbolKind.Function && s.kind !== SymbolKind.Method);

      result.push(...functions);
      result.push(...others);
    }

    return result;
  }

  private walkTree(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    // Handle declarations that can produce multiple symbols
    if (node.type === 'import_declaration') {
      const importSymbols = this.extractImportSymbols(node, parentId);
      symbols.push(...importSymbols);
    } else if (node.type === 'var_declaration') {
      const varSymbols = this.extractVarSymbols(node, parentId);
      symbols.push(...varSymbols);
    } else if (node.type === 'const_declaration') {
      const constSymbols = this.extractConstSymbols(node, parentId);
      symbols.push(...constSymbols);
    } else {
      const symbol = this.extractSymbol(node, parentId);
      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }
    }

    for (const child of node.children) {
      this.walkTree(child, symbols, parentId);
    }
  }

  protected extractSymbol(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    switch (node.type) {
      case 'package_clause':
        return this.extractPackage(node, parentId);
      case 'type_declaration':
        return this.extractTypeDeclaration(node, parentId);
      case 'function_declaration':
        return this.extractFunction(node, parentId);
      case 'method_declaration':
        return this.extractMethod(node, parentId);
      case 'field_declaration':
        return this.extractField(node, parentId);
      case 'ERROR':
        return this.extractFromErrorNode(node, parentId);
      default:
        return null;
    }
  }

  private extractImportSymbols(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const importSpecs = this.findImportSpecs(node);
    const symbols: Symbol[] = [];

    for (const spec of importSpecs) {
      const importPath = this.extractImportPath(spec);
      const alias = this.extractImportAlias(spec);

      // Skip blank imports (_)
      if (alias === '_') continue;

      const name = alias || this.getPackageNameFromPath(importPath);
      const signature = alias ? `import ${alias} "${importPath}"` : `import "${importPath}"`;

      const symbol = this.createSymbol(node, name, SymbolKind.Import, {
        signature,
        visibility: 'public',
        parentId
      });
      symbols.push(symbol);
    }

    return symbols;
  }

  private extractVarSymbols(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const varSpecs = this.findVarSpecs(node);
    const symbols: Symbol[] = [];

    for (const varSpec of varSpecs) {
      const nameNode = varSpec.children.find(c => c.type === 'identifier');
      if (!nameNode) continue;

      const name = this.getNodeText(nameNode);
      const typeAnnotation = this.extractVarType(varSpec);
      const initializer = this.extractVarInitializer(varSpec);

      let signature = `var ${name}`;
      if (typeAnnotation) signature += ` ${typeAnnotation}`;
      if (initializer) signature += ` = ${initializer}`;

      const symbol = this.createSymbol(node, name, SymbolKind.Variable, {
        signature,
        visibility: this.isPublic(name) ? 'public' : 'private',
        parentId
      });
      symbols.push(symbol);
    }

    return symbols;
  }

  private extractConstSymbols(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const constSpecs = this.findConstSpecs(node);
    const symbols: Symbol[] = [];

    for (const constSpec of constSpecs) {
      const nameNode = constSpec.children.find(c => c.type === 'identifier');
      if (!nameNode) continue;

      const name = this.getNodeText(nameNode);
      const value = this.extractConstValue(constSpec);

      const signature = `const ${name}${value ? ` = ${value}` : ''}`;

      const symbol = this.createSymbol(node, name, SymbolKind.Constant, {
        signature,
        visibility: this.isPublic(name) ? 'public' : 'private',
        parentId
      });
      symbols.push(symbol);
    }

    return symbols;
  }

  protected findDocComment(node: Parser.SyntaxNode): string | undefined {
    // Look for preceding comment
    const prevSibling = this.getPreviousSibling(node);
    if (prevSibling?.type === 'comment') {
      const commentText = this.getNodeText(prevSibling);
      // Go doc comments start with //
      if (commentText.startsWith('//')) {
        return commentText.substring(2).trim();
      }
    }
    return undefined;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      if (symbol.signature) {
        const type = this.inferTypeFromSignature(symbol.signature, symbol.kind);
        if (type) {
          typeMap.set(symbol.id, type);
        }
      }
    }

    return typeMap;
  }

  private extractPackage(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const packageNode = node.children.find(c => c.type === 'package_identifier');
    if (!packageNode) return null;

    const name = this.getNodeText(packageNode);
    const signature = `package ${name}`;

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractTypeDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Go type declarations can contain multiple type specs or type aliases
    const typeSpecs = node.children.filter(c => c.type === 'type_spec' || c.type === 'type_alias');

    // For now, handle the first type spec (we could extend to handle multiple)
    if (typeSpecs.length === 0) return null;

    const typeSpec = typeSpecs[0];
    const nameNode = typeSpec.children.find(c => c.type === 'type_identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check for generic type parameters
    const typeParamsNode = typeSpec.children.find(c => c.type === 'type_parameter_list');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    let kind: SymbolKind;
    let signature = `type ${name}${typeParams}`;

    // Handle type_alias differently from type_spec
    if (typeSpec.type === 'type_alias') {
      // Type alias: type Name = Type
      kind = SymbolKind.Type;
      const aliasTypeNode = typeSpec.children.find(c => c.type === 'type_identifier' && c !== nameNode);
      const aliasType = aliasTypeNode ? this.getNodeText(aliasTypeNode) : 'unknown';
      signature += ` = ${aliasType}`;
    } else {
      // Regular type_spec: handle struct, interface, or type definition
      const typeDefNode = typeSpec.children.find(c => c.type === 'struct_type') ||
                         typeSpec.children.find(c => c.type === 'interface_type') ||
                         typeSpec.children.find(c => c !== nameNode && c.type !== 'type_parameter_list' && c.type !== 'type');

      if (!typeDefNode) return null;

      switch (typeDefNode.type) {
        case 'struct_type':
          kind = SymbolKind.Class;
          signature += ' struct';
          break;
        case 'interface_type':
          kind = SymbolKind.Interface;
          signature += ' interface';

          // Extract interface body content (union types, methods, etc.)
          const typeElems = typeDefNode.children.filter(c => c.type === 'type_elem');
          if (typeElems.length > 0) {
            const typeElemContent = typeElems.map(elem => this.getNodeText(elem)).join('; ');
            signature += ` { ${typeElemContent} }`;
          }
          break;
        default:
          // Type definition: type Name Type - normalize to alias format for consistency
          kind = SymbolKind.Type;
          const aliasType = this.getNodeText(typeDefNode);
          signature += ` = ${aliasType}`;
          break;
      }
    }

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    // Check for generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameter_list');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    // Extract parameters and return type
    const params = this.extractFunctionParameters(node);
    const returnType = this.extractFunctionReturnType(node);

    // Build signature
    let signature = `func ${name}${typeParams}(${params.join(', ')})`;
    if (returnType) {
      signature += ` ${returnType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  private extractFromErrorNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Try to recover function signatures from ERROR nodes
    // Look for identifier + parenthesized_type pattern (function signature)
    const identifier = node.children.find(c => c.type === 'identifier');
    const paramType = node.children.find(c => c.type === 'parenthesized_type');

    if (identifier && paramType) {
      const name = this.getNodeText(identifier);
      const params = this.getNodeText(paramType);

      // This looks like a function signature trapped in an ERROR node
      const signature = `func ${name}${params}`;

      return this.createSymbol(node, name, SymbolKind.Function, {
        signature,
        visibility: this.isPublic(name) ? 'public' : 'private',
        parentId
      });
    }

    return null;
  }

  private extractField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract field name from field_identifier
    const nameNode = node.children.find(c => c.type === 'field_identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Extract field type from various type nodes
    const typeNode = node.children.find(c =>
      c.type === 'type_identifier' ||
      c.type === 'map_type' ||
      c.type === 'array_type' ||
      c.type === 'slice_type' ||
      c.type === 'channel_type' ||
      c.type === 'pointer_type' ||
      c.type === 'interface_type'
    );

    const fieldType = typeNode ? this.getNodeText(typeNode) : 'unknown';

    // Build signature
    const signature = `${name} ${fieldType}`;

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract receiver information
    const receiverList = node.children.find(c => c.type === 'parameter_list');
    const receiver = receiverList ? this.extractMethodReceiver(receiverList) : '';

    // Extract method name
    const nameNode = node.children.find(c => c.type === 'field_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    // Extract parameters (skip the first parameter_list which is the receiver)
    const paramLists = node.children.filter(c => c.type === 'parameter_list');
    const methodParams = paramLists.length > 1 ? this.extractParametersFromList(paramLists[1]) : [];

    // Extract return type
    const returnType = this.extractMethodReturnType(node);

    // Build signature
    let signature = `func ${receiver} ${name}(${methodParams.join(', ')})`;
    if (returnType) {
      signature += ` ${returnType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  private extractImportDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Go import declarations can be single or grouped
    const importSpecs = this.findImportSpecs(node);

    // For now, create one symbol for the import declaration
    // We could create individual symbols for each import spec
    if (importSpecs.length === 0) return null;

    const firstSpec = importSpecs[0];
    const importPath = this.extractImportPath(firstSpec);
    const alias = this.extractImportAlias(firstSpec);

    const name = alias || this.getPackageNameFromPath(importPath);
    const signature = alias ? `import ${alias} "${importPath}"` : `import "${importPath}"`;

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractConstDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract const specs from the declaration
    const constSpecs = node.children.filter(c => c.type === 'const_spec');

    // For now, handle the first const spec
    if (constSpecs.length === 0) return null;

    const constSpec = constSpecs[0];
    const nameNode = constSpec.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const value = this.extractConstValue(constSpec);

    const signature = value ? `const ${name} = ${value}` : `const ${name}`;

    return this.createSymbol(node, name, SymbolKind.Constant, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  private extractVarDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract var specs from the declaration
    const varSpecs = this.findVarSpecs(node);

    // For now, handle the first var spec
    if (varSpecs.length === 0) return null;

    const varSpec = varSpecs[0];
    const nameNode = varSpec.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const typeAnnotation = this.extractVarType(varSpec);
    const value = this.extractVarValue(varSpec);

    let signature = `var ${name}`;
    if (typeAnnotation) signature += ` ${typeAnnotation}`;
    if (value) signature += ` = ${value}`;

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: this.isPublic(name) ? 'public' : 'private',
      parentId
    });
  }

  // Helper methods for Go-specific parsing

  private isPublic(name: string): boolean {
    // In Go, identifiers starting with uppercase are public
    return name.length > 0 && name[0] === name[0].toUpperCase();
  }

  private extractFunctionParameters(node: Parser.SyntaxNode): string[] {
    const paramList = node.children.find(c => c.type === 'parameter_list');
    return paramList ? this.extractParametersFromList(paramList) : [];
  }

  private extractParametersFromList(paramList: Parser.SyntaxNode): string[] {
    const parameters: string[] = [];
    const paramDecls = paramList.children.filter(c =>
      c.type === 'parameter_declaration' || c.type === 'variadic_parameter_declaration'
    );

    for (const paramDecl of paramDecls) {
      const paramText = this.getNodeText(paramDecl);
      parameters.push(paramText);
    }

    return parameters;
  }

  private extractFunctionReturnType(node: Parser.SyntaxNode): string | undefined {
    // Look for return type after parameter list
    const children = node.children;
    const paramListIndex = children.findIndex(c => c.type === 'parameter_list');

    if (paramListIndex === -1) return undefined;

    // Check for return type after the parameter list
    for (let i = paramListIndex + 1; i < children.length; i++) {
      const child = children[i];
      if (child.type === 'block') break; // Reached function body
      if (child.type !== '(' && child.type !== ')') {
        return this.getNodeText(child);
      }
    }

    return undefined;
  }

  private extractMethodReceiver(receiverList: Parser.SyntaxNode): string {
    const paramDecl = receiverList.children.find(c => c.type === 'parameter_declaration');
    return paramDecl ? `(${this.getNodeText(paramDecl)})` : '';
  }

  private extractMethodReturnType(node: Parser.SyntaxNode): string | undefined {
    // For methods, return type comes after the method name and parameters
    const children = node.children;
    const nameIndex = children.findIndex(c => c.type === 'field_identifier');

    if (nameIndex === -1) return undefined;

    // Find all parameter_list nodes after the method name
    const paramLists = children.slice(nameIndex + 1).filter(c => c.type === 'parameter_list');

    // If there are 2+ parameter_lists after the name, the last one is the return type
    if (paramLists.length >= 2) {
      return this.getNodeText(paramLists[paramLists.length - 1]);
    }

    // Look for other return type nodes (non-parameter_list types)
    for (let i = nameIndex + 1; i < children.length; i++) {
      const child = children[i];
      if (child.type === 'block') break; // Reached method body
      if (child.type === 'parameter_list') continue; // Skip parameter list
      if (child.type !== '(' && child.type !== ')') {
        return this.getNodeText(child);
      }
    }

    return undefined;
  }

  private findImportSpecs(node: Parser.SyntaxNode): Parser.SyntaxNode[] {
    // Look for import_spec nodes in the tree
    const specs: Parser.SyntaxNode[] = [];
    this.collectNodesByType(node, 'import_spec', specs);
    return specs;
  }

  private collectNodesByType(node: Parser.SyntaxNode, type: string, collection: Parser.SyntaxNode[]) {
    if (node.type === type) {
      collection.push(node);
    }
    for (const child of node.children) {
      this.collectNodesByType(child, type, collection);
    }
  }

  private extractImportPath(importSpec: Parser.SyntaxNode): string {
    const stringLiteral = importSpec.children.find(c => c.type === 'interpreted_string_literal');
    if (stringLiteral) {
      const text = this.getNodeText(stringLiteral);
      // Remove quotes
      return text.slice(1, -1);
    }
    return '';
  }

  private extractImportAlias(importSpec: Parser.SyntaxNode): string | undefined {
    const identifier = importSpec.children.find(c => c.type === 'package_identifier');
    return identifier ? this.getNodeText(identifier) : undefined;
  }

  private getPackageNameFromPath(importPath: string): string {
    // Extract package name from import path (e.g., "net/http" -> "http")
    const parts = importPath.split('/');
    return parts[parts.length - 1];
  }

  private extractConstValue(constSpec: Parser.SyntaxNode): string | undefined {
    const expressionList = constSpec.children.find(c => c.type === 'expression_list');
    return expressionList ? this.getNodeText(expressionList) : undefined;
  }

  private findVarSpecs(node: Parser.SyntaxNode): Parser.SyntaxNode[] {
    const specs: Parser.SyntaxNode[] = [];
    this.collectNodesByType(node, 'var_spec', specs);
    return specs;
  }

  private extractVarType(varSpec: Parser.SyntaxNode): string | undefined {
    // Look for type between identifier and assignment
    const children = varSpec.children;
    const identifierIndex = children.findIndex(c => c.type === 'identifier');
    const assignIndex = children.findIndex(c => c.type === '=');

    if (identifierIndex === -1) return undefined;

    const searchEnd = assignIndex === -1 ? children.length : assignIndex;
    for (let i = identifierIndex + 1; i < searchEnd; i++) {
      const child = children[i];
      if (child.type !== '=' && child.type !== ',') {
        return this.getNodeText(child);
      }
    }

    return undefined;
  }

  private extractVarValue(varSpec: Parser.SyntaxNode): string | undefined {
    const expressionList = varSpec.children.find(c => c.type === 'expression_list');
    return expressionList ? this.getNodeText(expressionList) : undefined;
  }

  private inferTypeFromSignature(signature: string, kind: SymbolKind): string | null {
    // Extract return types from function signatures
    if (kind === SymbolKind.Function || kind === SymbolKind.Method) {
      // Look for return type after closing parenthesis
      const match = signature.match(/\)\s*(.+)$/);
      if (match) {
        return match[1].trim();
      }
    }

    // Extract types from variable declarations
    if (kind === SymbolKind.Variable) {
      const match = signature.match(/var\s+\w+\s+([^=]+)/);
      if (match) {
        return match[1].trim();
      }
    }

    return null;
  }

  private getPreviousSibling(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    if (!node.parent) return null;

    const siblings = node.parent.children;
    const index = siblings.indexOf(node);
    return index > 0 ? siblings[index - 1] : null;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map<string, Symbol>();

    // Build symbol lookup map
    symbols.forEach(symbol => {
      symbolMap.set(symbol.name, symbol);
    });

    // Extract relationships from the AST
    this.walkTreeForRelationships(tree.rootNode, symbolMap, relationships);

    return relationships;
  }

  private walkTreeForRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Handle interface implementations (implicit in Go)
    if (node.type === 'method_declaration') {
      this.extractMethodRelationships(node, symbolMap, relationships);
    }

    // Handle struct embedding
    if (node.type === 'struct_type') {
      this.extractEmbeddingRelationships(node, symbolMap, relationships);
    }

    // Recursively process children
    for (const child of node.children) {
      this.walkTreeForRelationships(child, symbolMap, relationships);
    }
  }

  private extractMethodRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Extract method to receiver type relationship
    const receiverList = node.children.find(c => c.type === 'parameter_list');
    if (!receiverList) return;

    const paramDecl = receiverList.children.find(c => c.type === 'parameter_declaration');
    if (!paramDecl) return;

    // Extract receiver type
    const receiverType = this.extractReceiverType(paramDecl);
    const receiverSymbol = symbolMap.get(receiverType);

    const nameNode = node.children.find(c => c.type === 'field_identifier');
    if (!nameNode) return;

    const methodName = this.getNodeText(nameNode);
    const methodSymbol = symbolMap.get(methodName);

    if (receiverSymbol && methodSymbol) {
      relationships.push({
        fromSymbolId: methodSymbol.id,
        toSymbolId: receiverSymbol.id,
        kind: RelationshipKind.Uses,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 0.9
      });
    }
  }

  private extractReceiverType(paramDecl: Parser.SyntaxNode): string {
    // Extract type from receiver parameter (handle *Type and Type)
    const children = paramDecl.children;
    for (const child of children) {
      if (child.type === 'type_identifier') {
        return this.getNodeText(child);
      } else if (child.type === 'pointer_type') {
        // Handle pointer types like *User
        const typeId = child.children.find(c => c.type === 'type_identifier');
        return typeId ? this.getNodeText(typeId) : '';
      }
    }
    return '';
  }

  private extractEmbeddingRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Go struct embedding creates implicit relationships
    // This would need more complex parsing to detect embedded types
    // For now, we'll skip this advanced feature
  }

  // Additional helper methods for variable and constant extraction
  private findConstSpecs(node: Parser.SyntaxNode): Parser.SyntaxNode[] {
    const specs: Parser.SyntaxNode[] = [];
    this.collectNodesByType(node, 'const_spec', specs);
    return specs;
  }

  private extractVarInitializer(varSpec: Parser.SyntaxNode): string | undefined {
    const assignIndex = varSpec.children.findIndex(c => c.type === '=');
    if (assignIndex === -1) return undefined;

    // Get everything after the '=' as the initializer
    const initNodes = varSpec.children.slice(assignIndex + 1);
    return initNodes.map(n => this.getNodeText(n)).join('').trim();
  }

  private extractConstValue(constSpec: Parser.SyntaxNode): string | undefined {
    const assignIndex = constSpec.children.findIndex(c => c.type === '=');
    if (assignIndex === -1) return undefined;

    // Get everything after the '=' as the value
    const valueNodes = constSpec.children.slice(assignIndex + 1);
    return valueNodes.map(n => this.getNodeText(n)).join('').trim();
  }
}