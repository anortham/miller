import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class PythonExtractor extends BaseExtractor {
  // Override BaseExtractor's findDocComment to handle Python docstrings
  protected findDocComment(node: Parser.SyntaxNode): string | undefined {
    // For Python, docstrings are string literals in function/class bodies
    if (node.type === 'function_definition' || node.type === 'class_definition') {
      return this.extractDocstring(node);
    }
    return undefined;
  }

  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_definition':
          symbol = this.extractClass(node, parentId);
          break;
        case 'function_definition':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'assignment':
          symbol = this.extractVariable(node, parentId);
          break;
        case 'import_statement':
        case 'import_from_statement':
          const importSymbols = this.extractImports(node, parentId);
          if (importSymbols.length > 0) {
            symbols.push(...importSymbols);
          }
          break;
        case 'decorated_definition':
          symbol = this.extractDecoratedDefinition(node, parentId);
          break;
        case 'lambda':
          symbol = this.extractLambda(node, parentId);
          break;
        case 'global_statement':
        case 'nonlocal_statement':
          symbol = this.extractScope(node, parentId);
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

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map(symbols.map(s => [s.name, s]));

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'class_definition':
          this.extractClassRelationships(node, symbolMap, relationships);
          break;
        case 'function_definition':
          this.extractFunctionRelationships(node, symbolMap, relationships);
          break;
        case 'call':
          this.extractCallRelationships(node, symbolMap, relationships);
          break;
        case 'attribute':
          this.extractAttributeRelationships(node, symbolMap, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      // Infer types from Python-specific patterns
      if (symbol.signature) {
        const type = this.inferTypeFromSignature(symbol.signature, symbol.kind);
        if (type) {
          typeMap.set(symbol.id, type);
        }
      }
    }

    return typeMap;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Extract base classes (inheritance)
    const superclasses = node.childForFieldName('superclasses');
    let extendsInfo = '';
    if (superclasses) {
      const bases = this.extractArgumentList(superclasses);
      if (bases.length > 0) {
        extendsInfo = ` extends ${bases.join(', ')}`;
      }
    }

    // Extract decorators
    const decorators = this.extractDecorators(node);
    const decoratorInfo = decorators.length > 0 ? `@${decorators.join(' @')} ` : '';

    const signature = `${decoratorInfo}class ${name}${extendsInfo}`;

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        decorators,
        superclasses: extendsInfo ? this.extractArgumentList(superclasses) : []
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Check if it's an async function
    const isAsync = this.hasAsyncKeyword(node);

    // Extract parameters
    const parametersNode = node.childForFieldName('parameters');
    const params = parametersNode ? this.extractParameters(parametersNode) : [];

    // Extract return type annotation
    const returnType = node.childForFieldName('return_type');
    const returnTypeStr = returnType ? `: ${this.getNodeText(returnType)}` : '';

    // Extract decorators
    const decorators = this.extractDecorators(node);
    const decoratorInfo = decorators.length > 0 ? `@${decorators.join(' @')} ` : '';

    const asyncPrefix = isAsync ? 'async ' : '';
    const signature = `${decoratorInfo}${asyncPrefix}def ${name}(${params.join(', ')})${returnTypeStr}`;

    // Determine if it's a method or function based on parent
    const kind = parentId ? SymbolKind.Method : SymbolKind.Function;

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: this.inferVisibility(name),
      parentId,
      metadata: {
        decorators,
        isAsync,
        returnType: returnTypeStr
      }
    });
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle assignments like: x = 5, x: int = 5, self.x = 5
    const left = node.childForFieldName('left');
    const right = node.childForFieldName('right');

    if (!left) return null;

    let name: string;
    let kind: SymbolKind = SymbolKind.Variable;

    if (left.type === 'identifier') {
      name = this.getNodeText(left);
    } else if (left.type === 'attribute') {
      // Handle self.attribute assignments
      const objectNode = left.childForFieldName('object');
      const attributeNode = left.childForFieldName('attribute');

      if (objectNode && this.getNodeText(objectNode) === 'self' && attributeNode) {
        name = this.getNodeText(attributeNode);
        kind = SymbolKind.Property;
      } else {
        return null; // Skip non-self attributes for now
      }
    } else if (left.type === 'pattern_list' || left.type === 'tuple_pattern') {
      // Handle multiple assignment: a, b = 1, 2
      return null; // TODO: Handle multiple assignments
    } else {
      return null;
    }

    // Check if it's a constant (uppercase name)
    if (name === name.toUpperCase() && name.length > 1) {
      kind = SymbolKind.Constant;
    }

    // Extract type annotation from assignment node
    let typeAnnotation = '';
    const typeNode = node.children.find(c => c.type === 'type');
    if (typeNode) {
      typeAnnotation = `: ${this.getNodeText(typeNode)}`;
    }

    // Extract value for signature
    const value = right ? this.getNodeText(right) : '';
    const signature = `${name}${typeAnnotation} = ${value}`;

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: this.inferVisibility(name),
      parentId
    });
  }

  private extractImports(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const imports: Symbol[] = [];

    if (node.type === 'import_statement') {
      // Handle single import: import module [as alias]
      const importSymbol = this.extractSingleImport(node, parentId);
      if (importSymbol) {
        imports.push(importSymbol);
      }
    } else if (node.type === 'import_from_statement') {
      // Handle from import: from module import name1, name2, name3
      const moduleNode = node.childForFieldName('module_name');
      if (!moduleNode) return imports;

      const module = this.getNodeText(moduleNode);

      // Find all dotted_name nodes after the 'import' keyword
      const importKeywordIndex = node.children.findIndex(c => c.text === 'import');
      if (importKeywordIndex === -1) return imports;

      // Collect all import names (dotted_name nodes after import keyword)
      const importNames = node.children.slice(importKeywordIndex + 1)
        .filter(c => c.type === 'dotted_name' || c.type === 'aliased_import');

      for (const importName of importNames) {
        if (importName.type === 'dotted_name') {
          // Simple import: from module import name
          const name = this.getNodeText(importName);
          const importText = `from ${module} import ${name}`;

          const symbol = this.createSymbol(node, name, SymbolKind.Import, {
            signature: importText,
            visibility: 'public',
            parentId
          });
          imports.push(symbol);
        } else if (importName.type === 'aliased_import') {
          // Aliased import: from module import name as alias
          const nameNode = importName.children.find(c => c.type === 'dotted_name');
          const aliasNode = importName.children.find(c => c.type === 'identifier');

          if (nameNode && aliasNode) {
            const importNameText = this.getNodeText(nameNode);
            const alias = this.getNodeText(aliasNode);
            const importText = `from ${module} import ${importNameText} as ${alias}`;

            const symbol = this.createSymbol(node, alias, SymbolKind.Import, {
              signature: importText,
              visibility: 'public',
              parentId
            });
            imports.push(symbol);
          }
        }
      }
    }

    return imports;
  }

  private extractSingleImport(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    let importText = '';
    let name = '';

    // Check for aliased_import child
    const aliasedImport = node.children.find(c => c.type === 'aliased_import');
    if (aliasedImport) {
      // import module as alias
      const nameNode = aliasedImport.children.find(c => c.type === 'dotted_name' || c.type === 'identifier');
      const aliasNode = aliasedImport.children[aliasedImport.children.length - 1]; // Last child is alias

      if (nameNode && aliasNode && aliasNode.type === 'identifier') {
        const moduleName = this.getNodeText(nameNode);
        const alias = this.getNodeText(aliasNode);
        importText = `import ${moduleName} as ${alias}`;
        name = alias; // Use alias as the symbol name
      }
    } else {
      // Simple import without alias
      const nameChild = node.children.find(c => c.type === 'dotted_name' || c.type === 'identifier');
      if (nameChild) {
        name = this.getNodeText(nameChild);
        importText = `import ${name}`;
      }
    }

    if (!name) return null;

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature: importText,
      visibility: 'public',
      parentId
    });
  }

  private extractDecoratedDefinition(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle @decorator functions/classes
    const definitionNode = node.childForFieldName('definition');
    if (!definitionNode) return null;

    // The actual symbol will be extracted when we visit the definition node
    // but we can extract decorator information here
    return null;
  }

  private extractLambda(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract lambda parameters
    const parametersNode = node.childForFieldName('parameters');
    const params = parametersNode ? this.extractParameters(parametersNode) : [];

    // Extract lambda body (simplified)
    const bodyNode = node.childForFieldName('body');
    const body = bodyNode ? this.getNodeText(bodyNode) : '';

    const signature = `lambda ${params.join(', ')}: ${body}`;
    const name = `<lambda:${node.startPosition.row}>`;

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractScope(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle global/nonlocal statements
    const nameNodes = node.children.filter(child => child.type === 'identifier');
    if (nameNodes.length === 0) return null;

    const names = nameNodes.map(n => this.getNodeText(n));
    const scopeType = node.type === 'global_statement' ? 'global' : 'nonlocal';
    const signature = `${scopeType} ${names.join(', ')}`;

    return this.createSymbol(node, `<${scopeType}>`, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  // Helper methods

  private extractDecorators(node: Parser.SyntaxNode): string[] {
    const decorators: string[] = [];
    let current = node;

    // Walk up to find decorated_definition parent
    while (current.parent && current.parent.type !== 'decorated_definition') {
      current = current.parent;
    }

    if (current.parent && current.parent.type === 'decorated_definition') {
      const decoratedNode = current.parent;
      for (const child of decoratedNode.children) {
        if (child.type === 'decorator') {
          const decoratorText = this.getNodeText(child).slice(1); // Remove @
          decorators.push(decoratorText);
        }
      }
    }

    return decorators;
  }

  private extractDocstring(node: Parser.SyntaxNode): string | undefined {
    const bodyNode = node.childForFieldName('body');
    if (!bodyNode) return undefined;

    // Look for first string in function body
    const firstChild = bodyNode.children.find(child =>
      child.type === 'expression_statement'
    );

    if (firstChild) {
      const exprNode = firstChild.childForFieldName('expression') || firstChild.children[0];
      if (exprNode && exprNode.type === 'string') {
        let docstring = this.getNodeText(exprNode);
        // Remove quotes (single, double, or triple quotes)
        if (docstring.startsWith('"""') && docstring.endsWith('"""')) {
          docstring = docstring.slice(3, -3);
        } else if (docstring.startsWith("'''") && docstring.endsWith("'''")) {
          docstring = docstring.slice(3, -3);
        } else if (docstring.startsWith('"') && docstring.endsWith('"')) {
          docstring = docstring.slice(1, -1);
        } else if (docstring.startsWith("'") && docstring.endsWith("'")) {
          docstring = docstring.slice(1, -1);
        }
        return docstring.trim();
      }
    }

    return undefined;
  }

  private hasAsyncKeyword(node: Parser.SyntaxNode): boolean {
    // Check if function has async keyword
    return node.children.some(child => child.type === 'async');
  }

  private extractParameters(parametersNode: Parser.SyntaxNode): string[] {
    const params: string[] = [];

    for (const child of parametersNode.children) {
      if (child.type === 'identifier') {
        // Simple parameter name
        params.push(this.getNodeText(child));
      } else if (child.type === 'parameter') {
        // Handle basic parameter
        const nameNode = child.children.find(c => c.type === 'identifier');
        if (nameNode) {
          params.push(this.getNodeText(nameNode));
        }
      } else if (child.type === 'default_parameter') {
        // parameter = default_value
        const nameNode = child.children.find(c => c.type === 'identifier');
        const name = nameNode ? this.getNodeText(nameNode) : '';
        // Find the value after '='
        const eqIndex = child.children.findIndex(c => c.type === '=');
        if (eqIndex >= 0 && eqIndex + 1 < child.children.length) {
          const valueNode = child.children[eqIndex + 1];
          const value = this.getNodeText(valueNode);
          params.push(`${name}=${value}`);
        } else {
          params.push(name);
        }
      } else if (child.type === 'typed_parameter') {
        // parameter: type
        const nameNode = child.children.find(c => c.type === 'identifier');
        const typeNode = child.children.find(c => c.type === 'type');
        const name = nameNode ? this.getNodeText(nameNode) : '';
        const type = typeNode ? this.getNodeText(typeNode) : '';
        params.push(`${name}: ${type}`);
      } else if (child.type === 'typed_default_parameter') {
        // parameter: type = default_value
        const nameNode = child.children.find(c => c.type === 'identifier');
        const typeNode = child.children.find(c => c.type === 'type');
        const name = nameNode ? this.getNodeText(nameNode) : '';
        const type = typeNode ? this.getNodeText(typeNode) : '';

        // Find the value after '='
        const eqIndex = child.children.findIndex(c => c.type === '=');
        if (eqIndex >= 0 && eqIndex + 1 < child.children.length) {
          const valueNode = child.children[eqIndex + 1];
          const value = this.getNodeText(valueNode);
          params.push(`${name}: ${type} = ${value}`);
        } else {
          params.push(`${name}: ${type}`);
        }
      } else if (child.type === 'list_splat_pattern' || child.type === 'dictionary_splat_pattern') {
        // Handle *args and **kwargs
        params.push(this.getNodeText(child));
      }
    }

    return params;
  }

  private extractArgumentList(node: Parser.SyntaxNode): string[] {
    const args: string[] = [];

    for (const child of node.children) {
      if (child.type === 'identifier' || child.type === 'attribute') {
        args.push(this.getNodeText(child));
      }
    }

    return args;
  }

  private inferVisibility(name: string): 'public' | 'private' | 'protected' {
    if (name.startsWith('__') && name.endsWith('__')) {
      return 'public'; // Dunder methods are public
    } else if (name.startsWith('_')) {
      return 'private'; // Single underscore indicates private/protected
    }
    return 'public';
  }

  private inferTypeFromSignature(signature: string, kind: SymbolKind): string | null {
    // Extract type hints from function signatures
    if (kind === SymbolKind.Function || kind === SymbolKind.Method) {
      const returnTypeMatch = signature.match(/:\s*([^=\s]+)\s*$/);
      if (returnTypeMatch) {
        return returnTypeMatch[1];
      }
    }

    // Extract type from variable annotations
    if (kind === SymbolKind.Variable || kind === SymbolKind.Property) {
      const typeMatch = signature.match(/:\s*([^=]+)\s*=/);
      if (typeMatch) {
        return typeMatch[1].trim();
      }
    }

    return null;
  }

  // Relationship extraction methods

  private extractClassRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    const nameNode = node.childForFieldName('name');
    if (!nameNode) return;

    const className = this.getNodeText(nameNode);
    const classSymbol = symbolMap.get(className);
    if (!classSymbol) return;

    // Extract inheritance relationships
    const superclasses = node.childForFieldName('superclasses');
    if (superclasses) {
      const bases = this.extractArgumentList(superclasses);
      for (const base of bases) {
        const baseSymbol = symbolMap.get(base);
        if (baseSymbol) {
          relationships.push({
            fromSymbolId: classSymbol.id,
            toSymbolId: baseSymbol.id,
            kind: RelationshipKind.Extends,
            filePath: this.filePath,
            lineNumber: node.startPosition.row + 1,
            confidence: 0.95
          });
        }
      }
    }
  }

  private extractFunctionRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Extract function call relationships
    const nameNode = node.childForFieldName('name');
    if (!nameNode) return;

    const functionName = this.getNodeText(nameNode);
    const functionSymbol = symbolMap.get(functionName);
    if (!functionSymbol) return;

    // Look for function calls within the function body
    this.findCallsInNode(node, functionSymbol, symbolMap, relationships);
  }

  private extractCallRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    const functionNode = node.childForFieldName('function');
    if (!functionNode) return;

    let calledName = '';
    if (functionNode.type === 'identifier') {
      calledName = this.getNodeText(functionNode);
    } else if (functionNode.type === 'attribute') {
      const attributeNode = functionNode.childForFieldName('attribute');
      if (attributeNode) {
        calledName = this.getNodeText(attributeNode);
      }
    }

    const calledSymbol = symbolMap.get(calledName);
    if (calledSymbol) {
      // Find the calling function by walking up the tree
      const caller = this.findEnclosingFunction(node, symbolMap);
      if (caller) {
        relationships.push({
          fromSymbolId: caller.id,
          toSymbolId: calledSymbol.id,
          kind: RelationshipKind.Calls,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 0.9
        });
      }
    }
  }

  private extractAttributeRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    const objectNode = node.childForFieldName('object');
    const attributeNode = node.childForFieldName('attribute');

    if (!objectNode || !attributeNode) return;

    const objectName = this.getNodeText(objectNode);
    const attributeName = this.getNodeText(attributeNode);

    const objectSymbol = symbolMap.get(objectName);
    const attributeSymbol = symbolMap.get(attributeName);

    if (objectSymbol && attributeSymbol) {
      relationships.push({
        fromSymbolId: objectSymbol.id,
        toSymbolId: attributeSymbol.id,
        kind: RelationshipKind.Uses,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 0.8
      });
    }
  }

  private findCallsInNode(
    node: Parser.SyntaxNode,
    callerSymbol: Symbol,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    if (node.type === 'call') {
      this.extractCallRelationships(node, symbolMap, relationships);
    }

    for (const child of node.children) {
      this.findCallsInNode(child, callerSymbol, symbolMap, relationships);
    }
  }

  private findEnclosingFunction(node: Parser.SyntaxNode, symbolMap: Map<string, Symbol>): Symbol | null {
    let current = node.parent;

    while (current) {
      if (current.type === 'function_definition') {
        const nameNode = current.childForFieldName('name');
        if (nameNode) {
          const functionName = this.getNodeText(nameNode);
          return symbolMap.get(functionName) || null;
        }
      }
      current = current.parent;
    }

    return null;
  }
}