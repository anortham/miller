import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class CExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'preproc_include':
          symbol = this.extractInclude(node, parentId);
          break;
        case 'preproc_def':
        case 'preproc_function_def':
          symbol = this.extractMacro(node, parentId);
          break;
        case 'declaration':
          const declarationSymbols = this.extractDeclaration(node, parentId);
          symbols.push(...declarationSymbols);
          break;
        case 'function_definition':
          symbol = this.extractFunctionDefinition(node, parentId);
          break;
        case 'struct_specifier':
          symbol = this.extractStruct(node, parentId);
          break;
        case 'enum_specifier':
          symbol = this.extractEnum(node, parentId);
          break;
        case 'type_definition':
          symbol = this.extractTypeDefinition(node, parentId);
          break;
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
      // If parsing fails completely, return empty symbols array
      console.warn('C parsing failed:', error);
    }
    return symbols;
  }

  private extractInclude(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const signature = this.getNodeText(node);
    const includePath = this.extractIncludePath(node);

    return this.createSymbol(node, includePath, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'include',
        path: includePath,
        isSystemHeader: this.isSystemHeader(signature)
      }
    });
  }

  private extractMacro(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const signature = this.getNodeText(node);
    const macroName = this.extractMacroName(node);

    return this.createSymbol(node, macroName, SymbolKind.Constant, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'macro',
        name: macroName,
        isFunctionLike: node.type === 'preproc_function_def',
        definition: signature
      }
    });
  }

  private extractDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const symbols: Symbol[] = [];

    // Check if this is a function declaration
    const functionDeclarator = this.findFunctionDeclarator(node);
    if (functionDeclarator) {
      const functionSymbol = this.extractFunctionDeclaration(node, parentId);
      if (functionSymbol) symbols.push(functionSymbol);
      return symbols;
    }

    // Extract variable declarations
    const declarators = this.findVariableDeclarators(node);
    for (const declarator of declarators) {
      const variableSymbol = this.extractVariableDeclaration(node, declarator, parentId);
      if (variableSymbol) symbols.push(variableSymbol);
    }

    return symbols;
  }

  private extractFunctionDefinition(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const functionName = this.extractFunctionName(node);
    const signature = this.buildFunctionSignature(node);

    return this.createSymbol(node, functionName, SymbolKind.Function, {
      signature,
      visibility: this.extractFunctionVisibility(node),
      parentId,
      metadata: {
        type: 'function',
        name: functionName,
        returnType: this.extractReturnType(node),
        parameters: this.extractFunctionParameters(node),
        isDefinition: true,
        isStatic: this.isStaticFunction(node)
      }
    });
  }

  private extractFunctionDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const functionName = this.extractFunctionNameFromDeclaration(node);
    const signature = this.buildFunctionDeclarationSignature(node);

    return this.createSymbol(node, functionName, SymbolKind.Function, {
      signature,
      visibility: this.extractFunctionVisibility(node),
      parentId,
      metadata: {
        type: 'function',
        name: functionName,
        returnType: this.extractReturnTypeFromDeclaration(node),
        parameters: this.extractFunctionParametersFromDeclaration(node),
        isDefinition: false,
        isStatic: this.isStaticFunction(node)
      }
    });
  }

  private extractVariableDeclaration(node: Parser.SyntaxNode, declarator: Parser.SyntaxNode, parentId?: string): Symbol {
    const variableName = this.extractVariableName(declarator);
    const signature = this.buildVariableSignature(node, declarator);

    return this.createSymbol(node, variableName, SymbolKind.Variable, {
      signature,
      visibility: this.extractVariableVisibility(node),
      parentId,
      metadata: {
        type: 'variable',
        name: variableName,
        dataType: this.extractVariableType(node),
        isStatic: this.isStaticVariable(node),
        isExtern: this.isExternVariable(node),
        isConst: this.isConstVariable(node),
        isVolatile: this.isVolatileVariable(node),
        isArray: this.isArrayVariable(declarator),
        initializer: this.extractInitializer(declarator)
      }
    });
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const structName = this.extractStructName(node);
    const signature = this.buildStructSignature(node);

    return this.createSymbol(node, structName, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'struct',
        name: structName,
        fields: this.extractStructFields(node)
      }
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const enumName = this.extractEnumName(node);
    const signature = this.buildEnumSignature(node);

    return this.createSymbol(node, enumName, SymbolKind.Enum, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'enum',
        name: enumName,
        values: this.extractEnumValues(node)
      }
    });
  }

  private extractTypedef(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const typedefName = this.extractTypedefName(node);
    const signature = this.getNodeText(node);

    return this.createSymbol(node, typedefName, SymbolKind.Type, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'typedef',
        name: typedefName,
        underlyingType: this.extractUnderlyingType(node)
      }
    });
  }

  private extractTypeDefinition(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const typedefName = this.extractTypedefNameFromTypeDefinition(node);
    const signature = this.getNodeText(node);
    const underlyingType = this.extractUnderlyingTypeFromTypeDefinition(node);

    // If the typedef contains an anonymous struct, treat it as a Class
    const symbolKind = this.containsAnonymousStruct(node) ? SymbolKind.Class : SymbolKind.Type;

    return this.createSymbol(node, typedefName, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: symbolKind === SymbolKind.Class ? 'struct' : 'typedef',
        name: typedefName,
        underlyingType: underlyingType,
        isAnonymousStruct: symbolKind === SymbolKind.Class
      }
    });
  }

  // Helper methods for extraction

  private extractIncludePath(node: Parser.SyntaxNode): string {
    const text = this.getNodeText(node);
    const match = text.match(/#include\s*[<"]([^>"]+)[>"]/);
    return match ? match[1] : 'unknown';
  }

  private isSystemHeader(signature: string): boolean {
    return signature.includes('<') && signature.includes('>');
  }

  private extractMacroName(node: Parser.SyntaxNode): string {
    const nameNode = node.children.find(c => c.type === 'identifier');
    return nameNode ? this.getNodeText(nameNode) : 'unknown';
  }

  private findFunctionDeclarator(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for function_declarator in the declaration
    for (const child of node.children) {
      if (child.type === 'function_declarator') {
        return child;
      }
      // Recursively search in declarator nodes
      if (child.type === 'init_declarator') {
        const funcDecl = child.children.find(c => c.type === 'function_declarator');
        if (funcDecl) return funcDecl;
      }
    }
    return null;
  }

  private findVariableDeclarators(node: Parser.SyntaxNode): Parser.SyntaxNode[] {
    const declarators: Parser.SyntaxNode[] = [];

    for (const child of node.children) {
      if (child.type === 'init_declarator') {
        declarators.push(child);
      } else if (child.type === 'declarator' || child.type === 'identifier') {
        declarators.push(child);
      }
    }

    return declarators;
  }

  private extractFunctionName(node: Parser.SyntaxNode): string {
    // Look for function declarator
    const declarator = node.children.find(c => c.type === 'function_declarator');
    if (declarator) {
      const identifier = declarator.children.find(c => c.type === 'identifier');
      if (identifier) {
        return this.getNodeText(identifier);
      }
    }
    return 'unknown';
  }

  private extractFunctionNameFromDeclaration(node: Parser.SyntaxNode): string {
    const functionDeclarator = this.findFunctionDeclarator(node);
    if (functionDeclarator) {
      const identifier = functionDeclarator.children.find(c => c.type === 'identifier');
      if (identifier) {
        return this.getNodeText(identifier);
      }
    }
    return 'unknown';
  }

  private extractVariableName(declarator: Parser.SyntaxNode): string {
    // Handle different declarator types
    if (declarator.type === 'identifier') {
      return this.getNodeText(declarator);
    }

    const identifier = this.findDeepestIdentifier(declarator);
    return identifier ? this.getNodeText(identifier) : 'unknown';
  }

  private findDeepestIdentifier(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    if (node.type === 'identifier') {
      return node;
    }

    for (const child of node.children) {
      const result = this.findDeepestIdentifier(child);
      if (result) return result;
    }

    return null;
  }

  private buildFunctionSignature(node: Parser.SyntaxNode): string {
    const returnType = this.extractReturnType(node);
    const functionName = this.extractFunctionName(node);
    const parameters = this.extractFunctionParameters(node);

    return `${returnType} ${functionName}(${parameters.join(', ')})`;
  }

  private buildFunctionDeclarationSignature(node: Parser.SyntaxNode): string {
    const returnType = this.extractReturnTypeFromDeclaration(node);
    const functionName = this.extractFunctionNameFromDeclaration(node);
    const parameters = this.extractFunctionParametersFromDeclaration(node);

    return `${returnType} ${functionName}(${parameters.join(', ')})`;
  }

  private buildVariableSignature(node: Parser.SyntaxNode, declarator: Parser.SyntaxNode): string {
    const storageClass = this.extractStorageClass(node);
    const typeQualifiers = this.extractTypeQualifiers(node);
    const dataType = this.extractVariableType(node);
    const variableName = this.extractVariableName(declarator);
    const arraySpec = this.extractArraySpecifier(declarator);
    const initializer = this.extractInitializer(declarator);

    let signature = '';
    if (storageClass) signature += `${storageClass} `;
    if (typeQualifiers) signature += `${typeQualifiers} `;
    signature += `${dataType} ${variableName}`;
    if (arraySpec) signature += arraySpec;
    if (initializer) signature += ` = ${initializer}`;

    return signature;
  }

  private buildStructSignature(node: Parser.SyntaxNode): string {
    const structName = this.extractStructName(node);
    const fields = this.extractStructFields(node);

    let signature = `struct ${structName}`;
    if (fields.length > 0) {
      const fieldSignatures = fields.slice(0, 3).map(f => `${f.type} ${f.name}`);
      signature += ` { ${fieldSignatures.join('; ')} }`;
    }

    return signature;
  }

  private buildEnumSignature(node: Parser.SyntaxNode): string {
    const enumName = this.extractEnumName(node);
    const values = this.extractEnumValues(node);

    let signature = `enum ${enumName}`;
    if (values.length > 0) {
      const valueNames = values.slice(0, 3).map(v => v.name);
      signature += ` { ${valueNames.join(', ')} }`;
    }

    return signature;
  }

  private extractReturnType(node: Parser.SyntaxNode): string {
    // Extract return type from function definition
    const typeNode = node.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'sized_type_specifier'
    );
    return typeNode ? this.getNodeText(typeNode) : 'void';
  }

  private extractReturnTypeFromDeclaration(node: Parser.SyntaxNode): string {
    // Similar to extractReturnType but for declarations
    const typeNode = node.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'sized_type_specifier'
    );
    return typeNode ? this.getNodeText(typeNode) : 'void';
  }

  private extractFunctionParameters(node: Parser.SyntaxNode): string[] {
    const declarator = node.children.find(c => c.type === 'function_declarator');
    if (!declarator) return [];

    const paramList = declarator.children.find(c => c.type === 'parameter_list');
    if (!paramList) return [];

    const parameters: string[] = [];
    for (const child of paramList.children) {
      if (child.type === 'parameter_declaration') {
        parameters.push(this.getNodeText(child));
      }
    }

    return parameters;
  }

  private extractFunctionParametersFromDeclaration(node: Parser.SyntaxNode): string[] {
    const functionDeclarator = this.findFunctionDeclarator(node);
    if (!functionDeclarator) return [];

    const paramList = functionDeclarator.children.find(c => c.type === 'parameter_list');
    if (!paramList) return [];

    const parameters: string[] = [];
    for (const child of paramList.children) {
      if (child.type === 'parameter_declaration') {
        parameters.push(this.getNodeText(child));
      }
    }

    return parameters;
  }

  private extractStorageClass(node: Parser.SyntaxNode): string | null {
    const storageClasses = ['static', 'extern', 'auto', 'register'];
    for (const child of node.children) {
      if (storageClasses.includes(child.type)) {
        return child.type;
      }
    }
    return null;
  }

  private extractTypeQualifiers(node: Parser.SyntaxNode): string | null {
    const qualifiers: string[] = [];
    const typeQualifiers = ['const', 'volatile', 'restrict'];

    for (const child of node.children) {
      if (typeQualifiers.includes(child.type)) {
        qualifiers.push(child.type);
      }
    }

    return qualifiers.length > 0 ? qualifiers.join(' ') : null;
  }

  private extractVariableType(node: Parser.SyntaxNode): string {
    const typeNode = node.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'sized_type_specifier' ||
      c.type === 'struct_specifier' ||
      c.type === 'enum_specifier'
    );
    return typeNode ? this.getNodeText(typeNode) : 'unknown';
  }

  private extractArraySpecifier(declarator: Parser.SyntaxNode): string | null {
    // Look for array declarator
    const arrayDecl = this.findNodeByType(declarator, 'array_declarator');
    if (arrayDecl) {
      const sizeNode = arrayDecl.children.find(c => c.type !== 'identifier');
      return sizeNode ? `[${this.getNodeText(sizeNode)}]` : '[]';
    }
    return null;
  }

  private extractInitializer(declarator: Parser.SyntaxNode): string | null {
    // Look for initializer in init_declarator
    if (declarator.type === 'init_declarator') {
      const initNode = declarator.children.find(c => c.type !== 'declarator' && c.text !== '=');
      return initNode ? this.getNodeText(initNode) : null;
    }
    return null;
  }

  private extractStructName(node: Parser.SyntaxNode): string {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    return nameNode ? this.getNodeText(nameNode) : 'anonymous';
  }

  private extractStructFields(node: Parser.SyntaxNode): Array<{name: string, type: string}> {
    const fields: Array<{name: string, type: string}> = [];
    const fieldList = node.children.find(c => c.type === 'field_declaration_list');

    if (fieldList) {
      for (const child of fieldList.children) {
        if (child.type === 'field_declaration') {
          const fieldType = this.extractVariableType(child);
          const declarators = this.findVariableDeclarators(child);

          for (const declarator of declarators) {
            const fieldName = this.extractVariableName(declarator);
            fields.push({ name: fieldName, type: fieldType });
          }
        }
      }
    }

    return fields;
  }

  private extractEnumName(node: Parser.SyntaxNode): string {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    return nameNode ? this.getNodeText(nameNode) : 'anonymous';
  }

  private extractEnumValues(node: Parser.SyntaxNode): Array<{name: string, value?: string}> {
    const values: Array<{name: string, value?: string}> = [];
    const enumList = node.children.find(c => c.type === 'enumerator_list');

    if (enumList) {
      for (const child of enumList.children) {
        if (child.type === 'enumerator') {
          const nameNode = child.children.find(c => c.type === 'identifier');
          if (nameNode) {
            const name = this.getNodeText(nameNode);
            const valueNode = child.children.find(c => c.type === 'number_literal');
            const value = valueNode ? this.getNodeText(valueNode) : undefined;
            values.push({ name, value });
          }
        }
      }
    }

    return values;
  }

  private extractTypedefName(node: Parser.SyntaxNode): string {
    // Look for the type identifier in typedef
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    return nameNode ? this.getNodeText(nameNode) : 'unknown';
  }

  private extractTypedefNameFromTypeDefinition(node: Parser.SyntaxNode): string {
    // For type_definition nodes, find the type name (last type_identifier or primitive_type)
    const children = node.children;

    // Look for the typedef name - it's typically the last identifier before semicolon
    for (let i = children.length - 1; i >= 0; i--) {
      const child = children[i];
      if (child.type === 'type_identifier') {
        const text = this.getNodeText(child);
        // Skip known attributes
        if (!['PACKED', 'ALIGNED', '__packed__', '__aligned__'].includes(text)) {
          return text;
        }
      } else if (child.type === 'primitive_type') {
        // Handle cases like "typedef unsigned long long uint64_t" where uint64_t is primitive_type
        return this.getNodeText(child);
      }
    }

    return 'unknown';
  }

  private extractUnderlyingTypeFromTypeDefinition(node: Parser.SyntaxNode): string {
    // Find the underlying type (first non-typedef child)
    for (const child of node.children) {
      if (child.type !== 'typedef' && child.type !== ';' && child.type !== 'type_identifier') {
        return this.getNodeText(child);
      }
    }
    return 'unknown';
  }

  private extractUnderlyingType(node: Parser.SyntaxNode): string {
    // Extract the underlying type from typedef
    const typeNode = node.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'struct_specifier' ||
      c.type === 'enum_specifier'
    );
    return typeNode ? this.getNodeText(typeNode) : 'unknown';
  }

  private containsAnonymousStruct(node: Parser.SyntaxNode): boolean {
    // Check if the typedef declaration contains an anonymous struct
    for (const child of node.children) {
      if (child.type === 'struct_specifier') {
        // Check if it's an anonymous struct (no type_identifier after 'struct' keyword)
        const hasName = child.children.some(c => c.type === 'type_identifier');
        if (!hasName) {
          return true;
        }
      }
    }
    return false;
  }

  // Utility methods

  private isStaticFunction(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'static');
  }

  private isStaticVariable(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'static');
  }

  private isExternVariable(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'extern');
  }

  private isConstVariable(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'const');
  }

  private isVolatileVariable(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'volatile');
  }

  private isArrayVariable(declarator: Parser.SyntaxNode): boolean {
    return this.findNodeByType(declarator, 'array_declarator') !== null;
  }

  private extractFunctionVisibility(node: Parser.SyntaxNode): string {
    return this.isStaticFunction(node) ? 'private' : 'public';
  }

  private extractVariableVisibility(node: Parser.SyntaxNode): string {
    return this.isStaticVariable(node) ? 'private' : 'public';
  }

  private findNodeByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode | null {
    if (node.type === type) return node;

    for (const child of node.children) {
      const result = this.findNodeByType(child, type);
      if (result) return result;
    }

    return null;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'call_expression':
          this.extractFunctionCallRelationships(node, symbols, relationships);
          break;
        case 'preproc_include':
          this.extractIncludeRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractFunctionCallRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const functionNode = node.children.find(c => c.type === 'identifier');
    if (functionNode) {
      const functionName = this.getNodeText(functionNode);
      const calledSymbol = symbols.find(s => s.name === functionName && s.kind === SymbolKind.Function);

      if (calledSymbol) {
        const containingSymbol = this.findContainingSymbol(node, symbols);
        if (containingSymbol) {
          relationships.push(this.createRelationship(
            containingSymbol.id,
            calledSymbol.id,
            RelationshipKind.Calls,
            node
          ));
        }
      }
    }
  }

  private extractIncludeRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const includePath = this.extractIncludePath(node);
    relationships.push({
      fromSymbolId: `file:${this.filePath}`,
      toSymbolId: `header:${includePath}`,
      kind: RelationshipKind.Imports,
      filePath: this.filePath,
      lineNumber: node.startPosition.row + 1,
      confidence: 1.0,
      metadata: { includePath }
    });
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      } else if (symbol.metadata?.returnType) {
        types.set(symbol.id, symbol.metadata.returnType);
      }
    }
    return types;
  }
}