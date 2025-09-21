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

    const visitNode = (node: Parser.SyntaxNode, parentId?: string, parentIsEnum: boolean = false) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_definition':
          symbol = this.extractClass(node, parentId);
          break;
        case 'function_definition':
          // Skip function definitions that are part of decorated definitions
          // (they will be handled by extractDecoratedDefinition)
          if (node.parent?.type === 'decorated_definition') {
            break;
          }
          symbol = this.extractFunction(node, parentId);
          break;
        case 'assignment':
          symbol = this.extractVariable(node, parentId, parentIsEnum);
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

      // Determine if we're entering an enum class for child traversal
      let isEnumClass = false;
      if (node.type === 'class_definition') {
        // Check if this class extends Enum
        const nameNode = node.childForFieldName('name');
        const className = nameNode ? this.getNodeText(nameNode) : 'Unknown';

        const superclasses = node.childForFieldName('superclasses');
        if (superclasses) {
          const allArgs = this.extractArgumentList(superclasses);
          const bases = allArgs.filter(arg => !arg.includes('='));
          isEnumClass = bases.some(base => base === 'Enum' || base.includes('Enum'));
        }
      }

      // Recursively visit children
      for (const child of node.children) {
        // For non-symbol-producing nodes, inherit the parent's enum status
        const inheritedEnumStatus = parentIsEnum || isEnumClass;
        visitNode(child, parentId, inheritedEnumStatus);
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
        case 'assignment':
          this.extractAssignmentRelationships(node, symbolMap, relationships);
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

    // Extract base classes and metaclass arguments
    const superclasses = node.childForFieldName('superclasses');
    let extendsInfo = '';
    let isEnum = false;
    let isProtocol = false;

    if (superclasses) {
      const allArgs = this.extractArgumentList(superclasses);
      const fullArgs = this.getNodeText(superclasses);

      // Separate regular base classes from keyword arguments
      const bases = allArgs.filter(arg => !arg.includes('='));
      const keywordArgs = allArgs.filter(arg => arg.includes('='));

      // Check if this is an Enum class
      isEnum = bases.some(base => base === 'Enum' || base.includes('Enum'));

      // Check if this is a Protocol class (should be treated as Interface)
      isProtocol = bases.some(base => base === 'Protocol' || base.includes('Protocol'));

      // Build extends information
      const extendsParts = [];
      if (bases.length > 0) {
        extendsParts.push(`extends ${bases.join(', ')}`);
      }

      // Add metaclass info if present
      const metaclassArg = keywordArgs.find(arg => arg.startsWith('metaclass='));
      if (metaclassArg) {
        extendsParts.push(metaclassArg);
      }

      if (extendsParts.length > 0) {
        extendsInfo = ` ${extendsParts.join(' ')}`;
      }
    }

    // Extract decorators
    const decorators = this.extractDecorators(node);
    const decoratorInfo = decorators.length > 0 ? `@${decorators.join(' @')} ` : '';

    const signature = `${decoratorInfo}class ${name}${extendsInfo}`;

    // Determine the symbol kind based on base classes
    let symbolKind = SymbolKind.Class;
    if (isEnum) {
      symbolKind = SymbolKind.Enum;
    } else if (isProtocol) {
      symbolKind = SymbolKind.Interface;
    }

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        decorators,
        superclasses: superclasses ? this.extractArgumentList(superclasses) : [],
        isEnum
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string, providedDecorators?: string[]): Symbol {
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

    // Extract decorators (use provided ones if available, otherwise extract from node)
    const decorators = providedDecorators || this.extractDecorators(node);
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

  private extractVariable(node: Parser.SyntaxNode, parentId?: string, parentIsEnum: boolean = false): Symbol | null {
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

    // Check if this is a special class attribute that should be a property
    if (name === '__slots__') {
      kind = SymbolKind.Property;
    }
    // Check if this is a property descriptor assignment
    else if (right && right.type === 'call') {
      const functionNode = right.childForFieldName('function');
      if (functionNode) {
        const functionName = this.getNodeText(functionNode);
        // Detect descriptor patterns like Descriptor(), property(), etc.
        if (functionName.includes('Descriptor') ||
            functionName.includes('descriptor') ||
            functionName === 'property' ||
            functionName.includes('Property')) {
          kind = SymbolKind.Property;
        }
      }
    }

    // Check if it's a constant (uppercase name) or enum member
    if (kind === SymbolKind.Variable && name === name.toUpperCase() && name.length > 1) {
      if (parentIsEnum) {
        kind = SymbolKind.EnumMember;
      } else {
        kind = SymbolKind.Constant;
      }
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

    // Extract decorators
    const decorators = this.extractDecorators(node);
    console.log(`DEBUG: extractDecoratedDefinition - decorators: [${decorators.join(', ')}]`);

    // Handle the definition based on its type
    if (definitionNode.type === 'function_definition') {
      // Check if this is a property (function with @property decorator)
      const isProperty = decorators.some(decorator =>
        decorator === 'property' || decorator.includes('property')
      );
      console.log(`DEBUG: isProperty check - isProperty: ${isProperty}`);

      if (isProperty) {
        // Extract as property instead of method
        const nameNode = definitionNode.childForFieldName('name');
        const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

        const parametersNode = definitionNode.childForFieldName('parameters');
        const parameters = parametersNode ? this.extractParameters(parametersNode) : [];
        const returnTypeNode = definitionNode.childForFieldName('return_type');
        const returnType = returnTypeNode ? `: ${this.getNodeText(returnTypeNode)}` : '';

        let signature = `@${decorators.join(' @')} def ${name}`;
        signature += `(${parameters.join(', ')})`;
        if (returnType) {
          signature += returnType;
        }

        // Extract docComment from the function definition
        const docComment = this.extractDocstring(definitionNode);

        return this.createSymbol(node, name, SymbolKind.Property, {
          signature,
          visibility: this.inferVisibility(name),
          parentId,
          docComment,
          metadata: {
            decorators,
            type: 'property',
            isProperty: true
          }
        });
      } else {
        // Extract as regular function/method with decorators
        return this.extractFunction(definitionNode, parentId, decorators);
      }
    } else if (definitionNode.type === 'class_definition') {
      // TODO: Handle decorated classes if needed
      return this.extractClass(definitionNode, parentId);
    }

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
    let decoratedNode: Parser.SyntaxNode | null = null;

    // Check if current node is already a decorated_definition
    if (node.type === 'decorated_definition') {
      decoratedNode = node;
    } else {
      // Walk up to find decorated_definition parent
      let current = node;
      while (current.parent && current.parent.type !== 'decorated_definition') {
        current = current.parent;
      }

      if (current.parent && current.parent.type === 'decorated_definition') {
        decoratedNode = current.parent;
      }
    }

    if (decoratedNode) {
      for (const child of decoratedNode.children) {
        if (child.type === 'decorator') {
          let decoratorText = this.getNodeText(child).slice(1); // Remove @

          // Extract just the decorator name without parameters
          // e.g., "lru_cache(maxsize=128)" -> "lru_cache"
          const parenIndex = decoratorText.indexOf('(');
          if (parenIndex !== -1) {
            decoratorText = decoratorText.substring(0, parenIndex);
          }

          decorators.push(decoratorText);
        }
      }
    }

    return decorators;
  }

  private extractDocstring(node: Parser.SyntaxNode): string | undefined {
    const bodyNode = node.childForFieldName('body');
    if (!bodyNode) return undefined;

    // Look for first string in function/class body (Python docstrings are direct string nodes)
    const firstChild = bodyNode.children.find(child =>
      child.type === 'string'
    );

    if (firstChild) {
      // firstChild is already the string node
      const exprNode = firstChild;
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
      } else if (child.type === 'subscript') {
        // Handle generic types like Generic[K, V]
        args.push(this.getNodeText(child));
      } else if (child.type === 'keyword_argument') {
        // Handle keyword arguments like metaclass=SingletonMeta
        const keywordNode = child.children.find(c => c.type === 'identifier');
        const valueNode = child.children[child.children.length - 1]; // Last child is the value
        if (keywordNode && valueNode && keywordNode.text === 'metaclass') {
          // For metaclass, we want to capture this as a special case for signature building
          args.push(`${keywordNode.text}=${valueNode.text}`);
        }
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
          // Determine relationship kind: implements for interfaces/protocols, extends for classes
          const relationshipKind = baseSymbol.kind === SymbolKind.Interface
            ? RelationshipKind.Implements
            : RelationshipKind.Extends;

          relationships.push({
            fromSymbolId: classSymbol.id,
            toSymbolId: baseSymbol.id,
            kind: relationshipKind,
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

    // For methods within classes, prefer class-level relationships for type usage
    const enclosingSymbol = this.findEnclosingSymbol(node, symbolMap) || functionSymbol;

    // Extract type annotation relationships from parameters
    const parametersNode = node.childForFieldName('parameters');
    if (parametersNode) {
      this.extractTypeAnnotationRelationships(parametersNode, enclosingSymbol, symbolMap, relationships);
    }

    // Extract return type relationships
    const returnTypeNode = node.childForFieldName('return_type');
    if (returnTypeNode) {
      this.extractTypeAnnotationRelationships(returnTypeNode, enclosingSymbol, symbolMap, relationships);
    }

    // Look for function calls within the function body
    this.findCallsInNode(node, functionSymbol, symbolMap, relationships);
  }

  private extractAssignmentRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Check if assignment has a type annotation
    const typeNode = node.children.find(c => c.type === 'type');
    if (typeNode) {
      // Find the enclosing class or function that contains this assignment
      const containingSymbol = this.findEnclosingSymbol(node, symbolMap);
      if (containingSymbol) {
        this.extractTypeAnnotationRelationships(typeNode, containingSymbol, symbolMap, relationships);
      }
    }
  }

  private findEnclosingSymbol(node: Parser.SyntaxNode, symbolMap: Map<string, Symbol>): Symbol | null {
    let classSymbol: Symbol | null = null;
    let functionSymbol: Symbol | null = null;

    let current = node.parent;
    while (current) {
      if (current.type === 'class_definition') {
        const nameNode = current.childForFieldName('name');
        if (nameNode) {
          const symbolName = this.getNodeText(nameNode);
          classSymbol = symbolMap.get(symbolName) || null;
        }
      } else if (current.type === 'function_definition' && !functionSymbol) {
        const nameNode = current.childForFieldName('name');
        if (nameNode) {
          const symbolName = this.getNodeText(nameNode);
          functionSymbol = symbolMap.get(symbolName) || null;
        }
      }
      current = current.parent;
    }

    // Prefer class symbol for 'uses' relationships to create class-level dependencies
    return classSymbol || functionSymbol;
  }

  private extractTypeAnnotationRelationships(
    node: Parser.SyntaxNode,
    fromSymbol: Symbol,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Extract type names from type annotations
    const typeNames = this.extractTypeNames(node);

    for (const typeName of typeNames) {
      const typeSymbol = symbolMap.get(typeName);
      if (typeSymbol && typeSymbol.id !== fromSymbol.id) {
        // Create 'uses' relationship
        const relationship = this.createRelationship(
          fromSymbol.id,
          typeSymbol.id,
          'uses',
          node
        );
        relationships.push(relationship);
      }
    }
  }

  private extractTypeNames(node: Parser.SyntaxNode): string[] {
    const typeNames: string[] = [];

    // Recursively extract type identifiers
    const extractFromNode = (n: Parser.SyntaxNode) => {
      if (n.type === 'identifier') {
        const name = this.getNodeText(n);
        // Avoid built-in types
        if (!this.isBuiltinType(name)) {
          typeNames.push(name);
        }
      } else if (n.type === 'subscript') {
        // Handle generic types like list[Shape], Dict[str, Shape]
        for (const child of n.children) {
          extractFromNode(child);
        }
      } else {
        // Recursively check children
        for (const child of n.children) {
          extractFromNode(child);
        }
      }
    };

    extractFromNode(node);
    return typeNames;
  }

  private isBuiltinType(typeName: string): boolean {
    const builtinTypes = [
      'int', 'float', 'str', 'bool', 'list', 'dict', 'tuple', 'set',
      'None', 'Any', 'Optional', 'Union', 'List', 'Dict', 'Tuple', 'Set'
    ];
    return builtinTypes.includes(typeName);
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