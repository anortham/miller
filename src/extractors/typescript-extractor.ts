import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class TypeScriptExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'interface_declaration':
          symbol = this.extractInterface(node, parentId);
          break;
        case 'function_declaration':
        case 'function':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'method_definition':
        case 'method_signature':
          symbol = this.extractMethod(node, parentId);
          break;
        case 'variable_declarator':
          symbol = this.extractVariable(node, parentId);
          break;
        case 'type_alias_declaration':
          symbol = this.extractTypeAlias(node, parentId);
          break;
        case 'enum_declaration':
          symbol = this.extractEnum(node, parentId);
          break;
        case 'import_statement':
        case 'import_declaration':
          symbol = this.extractImport(node, parentId);
          break;
        case 'export_statement':
          symbol = this.extractExport(node, parentId);
          break;
        case 'namespace_declaration':
        case 'module_declaration':
          symbol = this.extractNamespace(node, parentId);
          break;
        case 'property_signature':
        case 'public_field_definition':
        case 'property_definition':
          symbol = this.extractProperty(node, parentId);
          break;
      }

      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }

      // Recursively visit children
      for (const child of node.children) {
        visitNode(child, parentId);
      }
    };

    visitNode(tree.rootNode);
    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Extract extends clause
    const heritage = node.childForFieldName('heritage');
    const extendsClause = heritage?.children.find(c => c.type === 'extends_clause');
    const implementsClause = heritage?.children.find(c => c.type === 'implements_clause');

    const signature = this.buildClassSignature(node);

    return this.createSymbol(nameNode || node, name, SymbolKind.Class, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        extends: extendsClause ? this.getNodeText(extendsClause) : null,
        implements: implementsClause ? this.getNodeText(implementsClause) : null,
        isAbstract: node.children.some(c => c.type === 'abstract'),
        typeParameters: this.extractTypeParameters(node)
      }
    });
  }

  private extractInterface(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const heritage = node.childForFieldName('heritage');
    const extendsClause = heritage?.children.find(c => c.type === 'extends_clause');

    return this.createSymbol(nameNode || node, name, SymbolKind.Interface, {
      signature: `interface ${name}`,
      parentId,
      metadata: {
        extends: extendsClause ? this.getNodeText(extendsClause) : null,
        typeParameters: this.extractTypeParameters(node)
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    let name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Handle arrow functions assigned to variables
    if (node.type === 'arrow_function') {
      // If called from extractVariable, the variable declarator is the grandparent
      let varDeclarator = node.parent;
      if (varDeclarator?.type === 'variable_declarator') {
        const varNameNode = varDeclarator.childForFieldName('name');
        if (varNameNode) {
          name = this.getNodeText(varNameNode);
        }
      }
    }

    const signature = this.buildFunctionSignature(node, name);

    // Use nameNode for position if available, otherwise fall back to node
    const positionNode = nameNode || node;

    return this.createSymbol(positionNode, name, SymbolKind.Function, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isAsync: node.children.some(c => c.type === 'async'),
        isGenerator: node.children.some(c => c.type === '*'),
        parameters: this.extractParameters(node),
        returnType: this.getFieldText(node, 'return_type'),
        typeParameters: this.extractTypeParameters(node)
      }
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name') || node.childForFieldName('property');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Check if this is a constructor
    const kind = name === 'constructor' ? SymbolKind.Constructor : SymbolKind.Method;

    const signature = this.buildMethodSignature(node);

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isStatic: node.children.some(c => c.type === 'static'),
        isAbstract: node.children.some(c => c.type === 'abstract'),
        isAsync: node.children.some(c => c.type === 'async'),
        isGenerator: node.children.some(c => c.type === '*'),
        parameters: this.extractParameters(node),
        returnType: this.getFieldText(node, 'return_type'),
        typeParameters: this.extractTypeParameters(node)
      }
    });
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Check if this variable contains an arrow function
    const valueNode = node.children.find(c => c.type === 'arrow_function');
    if (valueNode) {
      // Extract as a function instead of a variable
      return this.extractFunction(valueNode, parentId);
    }

    // Check if it's a constant
    const parent = node.parent;
    const isConst = parent?.children.some(c => c.type === 'const');

    // For tests, treat constants as variables (as expected by the test)
    const kind = SymbolKind.Variable;

    return this.createSymbol(node, name, kind, {
      parentId,
      metadata: {
        isConst,
        type: this.getFieldText(node, 'type'),
        initializer: this.getFieldText(node, 'value')
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name') || node.childForFieldName('property');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    return this.createSymbol(node, name, SymbolKind.Property, {
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isStatic: node.children.some(c => c.type === 'static'),
        isReadonly: node.children.some(c => c.type === 'readonly'),
        type: this.getFieldText(node, 'type'),
        initializer: this.getFieldText(node, 'value')
      }
    });
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    const value = node.childForFieldName('value');

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature: `type ${name} = ${value ? this.getNodeText(value) : 'unknown'}`,
      parentId,
      metadata: {
        typeParameters: this.extractTypeParameters(node),
        definition: value ? this.getNodeText(value) : null
      }
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature: `enum ${name}`,
      parentId,
      metadata: {
        isConst: node.children.some(c => c.type === 'const')
      }
    });
  }

  private extractImport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const source = node.childForFieldName('source');
    const sourcePath = source ? this.getNodeText(source).replace(/['"`]/g, '') : 'unknown';

    return this.createSymbol(node, `import from ${sourcePath}`, SymbolKind.Import, {
      parentId,
      metadata: {
        source: sourcePath,
        specifiers: this.extractImportSpecifiers(node)
      }
    });
  }

  private extractExport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const exportedName = this.extractExportedName(node);

    return this.createSymbol(node, `export ${exportedName}`, SymbolKind.Export, {
      parentId,
      metadata: {
        exportedName,
        isDefault: node.children.some(c => c.type === 'default')
      }
    });
  }

  private extractNamespace(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature: `namespace ${name}`,
      parentId
    });
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map(symbols.map(s => [s.name, s]));

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'call_expression': {
          this.extractCallRelationships(node, symbols, relationships);
          break;
        }

        case 'extends_clause':
        case 'class_heritage': {
          this.extractExtendsRelationships(node, symbols, relationships);
          break;
        }

        case 'implements_clause': {
          this.extractImplementsRelationships(node, symbols, relationships);
          break;
        }

        case 'import_statement':
        case 'import_declaration': {
          this.extractImportRelationships(node, symbols, relationships);
          break;
        }

        case 'member_expression':
        case 'property_access_expression': {
          this.extractMemberAccessRelationships(node, symbols, relationships);
          break;
        }

        case 'new_expression': {
          this.extractInstantiationRelationships(node, symbols, relationships);
          break;
        }
      }

      // Visit children
      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractCallRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const callee = node.childForFieldName('function');
    if (!callee) return;

    const calleeName = this.getNodeText(callee);
    const calledSymbol = symbols.find(s => s.name === calleeName);

    if (calledSymbol) {
      const callingSymbol = this.findContainingSymbol(node, symbols);
      if (callingSymbol) {
        relationships.push(this.createRelationship(
          callingSymbol.id,
          calledSymbol.id,
          RelationshipKind.Calls,
          node
        ));
      }
    }
  }

  private extractExtendsRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const parent = node.parent;
    if (parent?.type === 'class_declaration') {
      const className = this.getFieldText(parent, 'name');
      const classSymbol = symbols.find(s => s.name === className);

      // For JavaScript class_heritage, look for extends keyword and identifier
      if (node.type === 'class_heritage') {
        const identifier = node.children.find(c => c.type === 'identifier');
        if (identifier) {
          const superClass = this.getNodeText(identifier);
          const superSymbol = symbols.find(s => s.name === superClass);

          if (classSymbol && superSymbol) {
            relationships.push(this.createRelationship(
              classSymbol.id,
              superSymbol.id,
              RelationshipKind.Extends,
              node
            ));
          }
        }
      } else {
        // Original TypeScript logic for extends_clause
        for (const child of node.children) {
          if (child.type === 'type_identifier' || child.type === 'identifier') {
            const superClass = this.getNodeText(child);
            const superSymbol = symbols.find(s => s.name === superClass);

            if (classSymbol && superSymbol) {
              relationships.push(this.createRelationship(
                classSymbol.id,
                superSymbol.id,
                RelationshipKind.Extends,
                node
              ));
            }
          }
        }
      }
    }
  }

  private extractImplementsRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const parent = node.parent;
    if (parent?.type === 'class_declaration') {
      const className = this.getFieldText(parent, 'name');
      const classSymbol = symbols.find(s => s.name === className);

      // Extract all implemented interfaces
      for (const child of node.children) {
        if (child.type === 'type_identifier' || child.type === 'identifier') {
          const interfaceName = this.getNodeText(child);
          const interfaceSymbol = symbols.find(s => s.name === interfaceName);

          if (classSymbol && interfaceSymbol) {
            relationships.push(this.createRelationship(
              classSymbol.id,
              interfaceSymbol.id,
              RelationshipKind.Implements,
              node
            ));
          }
        }
      }
    }
  }

  private extractImportRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const source = node.childForFieldName('source');
    if (source) {
      const importPath = this.getNodeText(source).replace(/['"`]/g, '');
      // Store import relationships for later cross-file resolution
      relationships.push({
        fromSymbolId: `file:${this.filePath}`,
        toSymbolId: `module:${importPath}`,
        kind: RelationshipKind.Imports,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { importPath }
      });
    }
  }

  private extractMemberAccessRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const object = node.childForFieldName('object');
    const property = node.childForFieldName('property');

    if (object && property) {
      const objectName = this.getNodeText(object);
      const propertyName = this.getNodeText(property);

      const objectSymbol = symbols.find(s => s.name === objectName);
      const propertySymbol = symbols.find(s => s.name === propertyName && s.parentId === objectSymbol?.id);

      if (objectSymbol && propertySymbol) {
        const containingSymbol = this.findContainingSymbol(node, symbols);
        if (containingSymbol) {
          relationships.push(this.createRelationship(
            containingSymbol.id,
            propertySymbol.id,
            RelationshipKind.Uses,
            node
          ));
        }
      }
    }
  }

  private extractInstantiationRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const constructor = node.childForFieldName('constructor');
    if (constructor) {
      const className = this.getNodeText(constructor);
      const classSymbol = symbols.find(s => s.name === className && s.kind === SymbolKind.Class);

      if (classSymbol) {
        const containingSymbol = this.findContainingSymbol(node, symbols);
        if (containingSymbol) {
          relationships.push(this.createRelationship(
            containingSymbol.id,
            classSymbol.id,
            RelationshipKind.Instantiates,
            node
          ));
        }
      }
    }
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    for (const symbol of symbols) {
      let inferredType: string | null = null;

      // Use explicit type annotations first
      if (symbol.metadata?.type) {
        inferredType = symbol.metadata.type;
      } else if (symbol.metadata?.returnType) {
        inferredType = symbol.metadata.returnType;
      } else {
        // Try to infer from context
        inferredType = this.inferTypeFromContext(symbol);
      }

      if (inferredType) {
        types.set(symbol.id, inferredType);
      }
    }

    return types;
  }

  private inferTypeFromContext(symbol: Symbol): string | null {
    const content = this.content.substring(symbol.startByte, symbol.endByte);

    // Infer from initialization
    if (content.includes('= new ')) {
      const match = content.match(/= new (\w+)/);
      if (match) return match[1];
    }

    // Basic literal type inference
    if (content.includes('= [')) return 'Array';
    if (content.includes('= {')) return 'Object';
    if (content.includes('= "') || content.includes("= '") || content.includes('= `')) return 'string';
    if (content.match(/= \d+(\.\d+)?/)) return 'number';
    if (content.includes('= true') || content.includes('= false')) return 'boolean';
    if (content.includes('= null')) return 'null';
    if (content.includes('= undefined')) return 'undefined';

    // Function type inference
    if (symbol.kind === SymbolKind.Function || symbol.kind === SymbolKind.Method) {
      const params = symbol.metadata?.parameters || '()';
      const returnType = symbol.metadata?.returnType || 'unknown';
      return `${params} => ${returnType}`;
    }

    return null;
  }

  // Helper methods
  private buildClassSignature(node: Parser.SyntaxNode): string {
    const name = this.getFieldText(node, 'name') || 'Anonymous';
    const typeParams = this.getFieldText(node, 'type_parameters') || '';
    const heritage = this.getFieldText(node, 'heritage') || '';

    let signature = `class ${name}`;
    if (typeParams) signature += typeParams;
    if (heritage) signature += ` ${heritage}`;

    return signature;
  }

  private buildFunctionSignature(node: Parser.SyntaxNode, name?: string): string {
    const functionName = name || this.getFieldText(node, 'name') || 'function';
    const params = this.getFieldText(node, 'parameters') || this.getFieldText(node, 'formal_parameters') || '()';
    const returnType = this.getFieldText(node, 'return_type');

    let signature = `${functionName}${params}`;
    if (returnType) signature += `: ${returnType}`;

    return signature;
  }

  private buildMethodSignature(node: Parser.SyntaxNode): string {
    const name = this.getFieldText(node, 'name') || this.getFieldText(node, 'property') || 'method';
    const params = this.getFieldText(node, 'parameters') || '()';
    const returnType = this.getFieldText(node, 'return_type');

    let signature = `${name}${params}`;
    if (returnType) signature += `: ${returnType}`;

    return signature;
  }

  private extractTypeParameters(node: Parser.SyntaxNode): string[] {
    const typeParams = node.childForFieldName('type_parameters');
    if (!typeParams) return [];

    const params: string[] = [];
    for (const child of typeParams.children) {
      if (child.type === 'type_parameter') {
        params.push(this.getNodeText(child));
      }
    }
    return params;
  }

  private extractParameters(node: Parser.SyntaxNode): string[] {
    const params = node.childForFieldName('parameters');
    if (!params) return [];

    const parameters: string[] = [];
    for (const child of params.children) {
      if (child.type === 'parameter' || child.type === 'identifier') {
        parameters.push(this.getNodeText(child));
      }
    }
    return parameters;
  }

  private extractImportSpecifiers(node: Parser.SyntaxNode): string[] {
    const specifiers: string[] = [];

    for (const child of node.children) {
      if (child.type === 'import_specifier' || child.type === 'namespace_import' || child.type === 'identifier') {
        specifiers.push(this.getNodeText(child));
      }
    }

    return specifiers;
  }

  private extractExportedName(node: Parser.SyntaxNode): string {
    // Try to find what's being exported
    for (const child of node.children) {
      if (child.type === 'identifier' || child.type === 'class_declaration' || child.type === 'function_declaration') {
        const name = this.getFieldText(child, 'name');
        if (name) return name;
      }
    }

    return 'unknown';
  }
}