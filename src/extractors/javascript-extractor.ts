import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class JavaScriptExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'function_declaration':
        case 'function':
        case 'arrow_function':
        case 'function_expression':
        case 'generator_function':
        case 'generator_function_declaration':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'method_definition':
          symbol = this.extractMethod(node, parentId);
          break;
        case 'variable_declarator':
          // Handle destructuring patterns that create multiple symbols
          const nameNode = node.childForFieldName('name');
          if (nameNode?.type === 'object_pattern' || nameNode?.type === 'array_pattern') {
            const destructuredSymbols = this.extractDestructuringVariables(node, parentId);
            symbols.push(...destructuredSymbols);
          } else {
            symbol = this.extractVariable(node, parentId);
          }
          break;
        case 'import_statement':
        case 'import_declaration':
          // Handle multiple import specifiers
          const importSymbols = this.extractImportSpecifiers(node).map(specifier =>
            this.createImportSymbol(node, specifier, parentId)
          );
          symbols.push(...importSymbols);
          break;
        case 'export_statement':
        case 'export_declaration':
          symbol = this.extractExport(node, parentId);
          break;
        case 'property_definition':
        case 'public_field_definition':
        case 'field_definition':
        case 'pair':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'assignment_expression':
          symbol = this.extractAssignment(node, parentId);
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
    const heritage = node.childForFieldName('heritage') ||
                     node.children.find(c => c.type === 'class_heritage');
    const extendsClause = heritage?.children.find(c => c.type === 'extends_clause');

    const signature = this.buildClassSignature(node);

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        extends: extendsClause ? this.getNodeText(extendsClause) : null,
        isGenerator: false, // JavaScript classes are not generators
        hasPrivateFields: this.hasPrivateFields(node)
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    let name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Handle arrow functions assigned to variables
    if (node.type === 'arrow_function' || node.type === 'function_expression') {
      // If called from extractVariable, get name from variable declarator
      let varDeclarator = node.parent;
      if (varDeclarator?.type === 'variable_declarator') {
        const varNameNode = varDeclarator.childForFieldName('name');
        if (varNameNode) {
          name = this.getNodeText(varNameNode);
        }
      } else if (varDeclarator?.type === 'assignment_expression') {
        // Handle assignments like: const func = () => {}
        const leftNode = varDeclarator.childForFieldName('left');
        if (leftNode) {
          name = this.getNodeText(leftNode);
        }
      } else if (varDeclarator?.type === 'pair') {
        // Handle object method shorthand
        const keyNode = varDeclarator.childForFieldName('key');
        if (keyNode) {
          name = this.getNodeText(keyNode);
        }
      }
    }

    const signature = this.buildFunctionSignature(node, name);

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isAsync: this.isAsync(node),
        isGenerator: this.isGenerator(node),
        isArrowFunction: node.type === 'arrow_function',
        parameters: this.extractParameters(node),
        isExpression: node.type === 'function_expression'
      }
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name') ||
                     node.childForFieldName('property') ||
                     node.childForFieldName('key');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const signature = this.buildMethodSignature(node, name);

    let symbolKind = SymbolKind.Method;

    // Determine if it's a constructor
    if (name === 'constructor') {
      symbolKind = SymbolKind.Constructor;
    }

    // Check for getters and setters
    const isGetter = node.children.some(c => c.type === 'get');
    const isSetter = node.children.some(c => c.type === 'set');

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isStatic: node.children.some(c => c.type === 'static'),
        isAsync: this.isAsync(node),
        isGenerator: this.isGenerator(node),
        isGetter,
        isSetter,
        isPrivate: name.startsWith('#'),
        parameters: this.extractParameters(node)
      }
    });
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const valueNode = node.childForFieldName('value');
    const signature = this.buildVariableSignature(node, name);

    // Check if this is a destructured require like: const { promisify } = require('util')
    if (nameNode?.type === 'object_pattern' && valueNode && this.isRequireCall(valueNode)) {
      // This is handled by extractRequireDestructuring - return the first extracted symbol
      return this.extractRequireDestructuring(node, parentId);
    }

    // Check if this is a CommonJS require statement
    if (valueNode && this.isRequireCall(valueNode)) {
      return this.createSymbol(node, name, SymbolKind.Import, {
        signature,
        visibility: this.extractVisibility(node),
        parentId,
        metadata: {
          source: this.extractRequireSource(valueNode),
          isCommonJS: true
        }
      });
    }

    // For function expressions, create a function symbol with the variable's name
    if (valueNode && (valueNode.type === 'arrow_function' ||
                      valueNode.type === 'function_expression' ||
                      valueNode.type === 'generator_function')) {
      return this.createSymbol(node, name, SymbolKind.Function, {
        signature,
        visibility: this.extractVisibility(node),
        parentId,
        metadata: {
          isAsync: this.isAsync(valueNode),
          isGenerator: this.isGenerator(valueNode),
          isArrowFunction: valueNode.type === 'arrow_function',
          isExpression: true,
          parameters: this.extractParameters(valueNode)
        }
      });
    }

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        declarationType: this.getDeclarationType(node),
        initializer: valueNode ? this.getNodeText(valueNode) : null,
        isConst: this.isConstDeclaration(node),
        isLet: this.isLetDeclaration(node)
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('key') ||
                     node.childForFieldName('name') ||
                     node.childForFieldName('property');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const valueNode = node.childForFieldName('value');
    const signature = this.buildPropertySignature(node, name);

    // If the value is a function, treat it as a method
    if (valueNode && (valueNode.type === 'arrow_function' ||
                      valueNode.type === 'function_expression' ||
                      valueNode.type === 'generator_function')) {
      return this.createSymbol(node, name, SymbolKind.Method, {
        signature: this.buildMethodSignature(valueNode, name),
        visibility: this.extractVisibility(node),
        parentId,
        metadata: {
          isAsync: this.isAsync(valueNode),
          isGenerator: this.isGenerator(valueNode),
          parameters: this.extractParameters(valueNode)
        }
      });
    }

    // Determine if this is a class field or regular property
    const symbolKind = (node.type === 'public_field_definition' ||
                        node.type === 'field_definition' ||
                        node.type === 'property_definition') ? SymbolKind.Field : SymbolKind.Property;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        value: valueNode ? this.getNodeText(valueNode) : null,
        isComputed: this.isComputedProperty(node),
        isPrivate: name.startsWith('#')
      }
    });
  }

  private createImportSymbol(node: Parser.SyntaxNode, specifier: string, parentId?: string): Symbol {
    const source = node.childForFieldName('source');
    const sourcePath = source ? this.getNodeText(source).replace(/['"`]/g, '') : 'unknown';

    return this.createSymbol(node, specifier, SymbolKind.Import, {
      signature: this.getNodeText(node),
      parentId,
      metadata: {
        source: sourcePath,
        specifier,
        isDefault: this.hasDefaultImport(node),
        isNamespace: this.hasNamespaceImport(node)
      }
    });
  }

  private extractExport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const exportedName = this.extractExportedName(node);
    const signature = this.getNodeText(node);

    return this.createSymbol(node, exportedName, SymbolKind.Export, {
      signature,
      parentId,
      metadata: {
        exportedName,
        isDefault: this.isDefaultExport(node),
        isNamed: this.isNamedExport(node)
      }
    });
  }

  private extractAssignment(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const leftNode = node.childForFieldName('left');
    const rightNode = node.childForFieldName('right');

    if (!leftNode) return null;

    // Handle member expression assignments like: Constructor.prototype.method = function() {} or Constructor.method = function() {}
    if (leftNode.type === 'member_expression') {
      const objectNode = leftNode.childForFieldName('object');
      const propertyNode = leftNode.childForFieldName('property');

      if (objectNode && propertyNode) {
        const objectText = this.getNodeText(objectNode);
        const propertyName = this.getNodeText(propertyNode);
        const signature = this.getNodeText(node);

        // Check if this is a prototype assignment
        if (objectText.includes('.prototype')) {
          return this.createSymbol(node, propertyName, SymbolKind.Method, {
            signature,
            visibility: 'public',
            parentId,
            metadata: {
              isPrototypeMethod: true,
              isFunction: rightNode?.type === 'function_expression' || rightNode?.type === 'arrow_function'
            }
          });
        }
        // Check if this is a static method assignment (e.g., Calculator.create = function() {})
        else if (rightNode?.type === 'function_expression' || rightNode?.type === 'arrow_function') {
          return this.createSymbol(node, propertyName, SymbolKind.Method, {
            signature,
            visibility: 'public',
            parentId,
            metadata: {
              isStaticMethod: true,
              isFunction: true,
              className: objectText
            }
          });
        }
      }
    }

    return null;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'call_expression':
          this.extractCallRelationships(node, symbols, relationships);
          break;
        case 'extends_clause':
        case 'class_heritage':
          this.extractExtendsRelationships(node, symbols, relationships);
          break;
        case 'import_statement':
        case 'import_declaration':
          this.extractImportRelationships(node, symbols, relationships);
          break;
        case 'member_expression':
          this.extractMemberAccessRelationships(node, symbols, relationships);
          break;
        case 'new_expression':
          this.extractInstantiationRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  // Helper methods
  private buildClassSignature(node: Parser.SyntaxNode): string {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    let signature = `class ${name}`;

    // Look for extends clause - check class_heritage and direct extends
    const heritage = node.childForFieldName('superclass') ||
                     node.children.find(c => c.type === 'class_heritage');

    if (heritage) {
      if (heritage.type === 'identifier') {
        // Direct superclass reference
        signature += ` extends ${this.getNodeText(heritage)}`;
      } else {
        // Look within class_heritage for extends_clause or identifier
        for (const child of heritage.children) {
          if (child.type === 'identifier') {
            signature += ` extends ${this.getNodeText(child)}`;
            break;
          }
        }
      }
    }

    return signature;
  }

  private buildFunctionSignature(node: Parser.SyntaxNode, name: string): string {
    const isAsync = this.isAsync(node);
    const isGenerator = this.isGenerator(node);
    const parameters = this.extractParameters(node);

    let signature = '';

    if (isAsync) signature += 'async ';

    if (node.type === 'arrow_function') {
      if (isGenerator) signature += 'function* ';
      signature += `${name} = (${parameters.join(', ')}) => `;
    } else if (node.type === 'function_expression') {
      if (isGenerator) signature += 'function* ';
      else signature += 'function ';
      signature += `${name}(${parameters.join(', ')})`;
    } else {
      if (isGenerator) signature += 'function* ';
      else signature += 'function ';
      signature += `${name}(${parameters.join(', ')})`;
    }

    return signature;
  }

  private buildMethodSignature(node: Parser.SyntaxNode, name: string): string {
    const isAsync = this.isAsync(node);
    const isGenerator = this.isGenerator(node);
    const isStatic = node.children.some(c => c.type === 'static');
    const isGetter = node.children.some(c => c.type === 'get');
    const isSetter = node.children.some(c => c.type === 'set');
    const parameters = this.extractParameters(node);

    let signature = '';

    if (isStatic) signature += 'static ';
    if (isAsync) signature += 'async ';
    if (isGetter) signature += 'get ';
    if (isSetter) signature += 'set ';
    if (isGenerator) signature += '*';

    signature += `${name}(${parameters.join(', ')})`;

    return signature;
  }

  private buildVariableSignature(node: Parser.SyntaxNode, name: string): string {
    const declarationType = this.getDeclarationType(node);
    const valueNode = node.childForFieldName('value');

    let signature = `${declarationType} ${name}`;

    if (valueNode) {
      // For function expressions, show a cleaner signature
      if (valueNode.type === 'function_expression') {
        signature += ` = function`;
        const params = this.extractParameters(valueNode);
        signature += `(${params.join(', ')})`;
      } else if (valueNode.type === 'arrow_function') {
        // Check if async
        const isAsync = this.isAsync(valueNode);
        if (isAsync) {
          signature += ` = async `;
        } else {
          signature += ` = `;
        }

        const params = this.extractParameters(valueNode);
        signature += `(${params.join(', ')}) =>`;

        // For simple arrow functions, include the body if it's a simple expression
        const bodyNode = valueNode.children.find(c =>
          c.type === 'expression' ||
          c.type === 'binary_expression' ||
          c.type === 'call_expression' ||
          c.type === 'identifier' ||
          c.type === 'number' ||
          c.type === 'string'
        );

        if (bodyNode) {
          const bodyText = this.getNodeText(bodyNode);
          if (bodyText.length <= 30) {
            signature += ` ${bodyText}`;
          }
        }
      } else {
        const valueText = this.getNodeText(valueNode);
        // Truncate very long values
        const truncatedValue = valueText.length > 50 ? valueText.substring(0, 50) + '...' : valueText;
        signature += ` = ${truncatedValue}`;
      }
    }

    return signature;
  }

  private buildPropertySignature(node: Parser.SyntaxNode, name: string): string {
    const valueNode = node.childForFieldName('value');

    let signature = name;

    if (valueNode) {
      const valueText = this.getNodeText(valueNode);
      const truncatedValue = valueText.length > 30 ? valueText.substring(0, 30) + '...' : valueText;
      signature += `: ${truncatedValue}`;
    }

    return signature;
  }

  private getDeclarationType(node: Parser.SyntaxNode): string {
    let current = node.parent;
    while (current) {
      if (current.type === 'variable_declaration' || current.type === 'lexical_declaration') {
        // Look for the keyword in the first child
        const firstChild = current.children[0];
        if (firstChild && ['const', 'let', 'var'].includes(firstChild.text)) {
          return firstChild.text;
        }
        // Fallback: look through all children for keywords
        for (const child of current.children) {
          if (['const', 'let', 'var'].includes(child.text)) {
            return child.text;
          }
        }
      }
      current = current.parent;
    }
    return 'var';
  }

  private isAsync(node: Parser.SyntaxNode): boolean {
    // Direct check: node has async child
    if (node.children.some(c => c.text === 'async' || c.type === 'async')) {
      return true;
    }

    // For arrow functions: check if first child is async
    if (node.type === 'arrow_function' && node.children.length > 0 && node.children[0].text === 'async') {
      return true;
    }

    // For function expressions and arrow functions assigned to variables,
    // check if the parent variable declaration has async before the arrow function
    let current = node.parent;
    while (current && current.type !== 'program') {
      if (current.children.some(c => c.text === 'async')) {
        return true;
      }
      current = current.parent;
    }

    return false;
  }

  private isGenerator(node: Parser.SyntaxNode): boolean {
    return node.type.includes('generator') ||
           node.children.some(c => c.type === '*') ||
           node.parent?.children.some(c => c.type === '*') === true;
  }

  private isConstDeclaration(node: Parser.SyntaxNode): boolean {
    return this.getDeclarationType(node) === 'const';
  }

  private isLetDeclaration(node: Parser.SyntaxNode): boolean {
    return this.getDeclarationType(node) === 'let';
  }

  private hasPrivateFields(node: Parser.SyntaxNode): boolean {
    // Check if class has any private fields (starting with #)
    for (const child of node.children) {
      if (child.type === 'class_body') {
        for (const member of child.children) {
          const nameNode = member.childForFieldName('name') || member.childForFieldName('property');
          if (nameNode && this.getNodeText(nameNode).startsWith('#')) {
            return true;
          }
        }
      }
    }
    return false;
  }

  private isComputedProperty(node: Parser.SyntaxNode): boolean {
    const keyNode = node.childForFieldName('key');
    return keyNode?.type === 'computed_property_name';
  }

  private hasDefaultImport(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'import_default_specifier');
  }

  private hasNamespaceImport(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'namespace_import');
  }

  private isDefaultExport(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'default');
  }

  private isNamedExport(node: Parser.SyntaxNode): boolean {
    return !this.isDefaultExport(node);
  }

  private extractParameters(node: Parser.SyntaxNode): string[] {
    // Look for formal_parameters node
    const formalParams = node.children.find(c => c.type === 'formal_parameters');
    if (!formalParams) return [];

    const parameters: string[] = [];
    for (const child of formalParams.children) {
      if (child.type === 'identifier' ||
          child.type === 'rest_pattern' ||
          child.type === 'object_pattern' ||
          child.type === 'array_pattern' ||
          child.type === 'assignment_pattern' ||
          child.type === 'object_assignment_pattern' ||
          child.type === 'shorthand_property_identifier_pattern') {
        parameters.push(this.getNodeText(child));
      }
    }
    return parameters;
  }

  private extractImportSpecifiers(node: Parser.SyntaxNode): string[] {
    const specifiers: string[] = [];

    // Look for import clause which contains the specifiers
    const importClause = node.children.find(c => c.type === 'import_clause');
    if (importClause) {
      for (const child of importClause.children) {
        if (child.type === 'import_specifier') {
          // For named imports like { debounce, throttle }
          const nameNode = child.childForFieldName('name');
          const aliasNode = child.childForFieldName('alias');

          if (nameNode) {
            specifiers.push(this.getNodeText(nameNode));
          }
          if (aliasNode) {
            specifiers.push(this.getNodeText(aliasNode));
          }
        } else if (child.type === 'identifier') {
          // For default imports like React
          specifiers.push(this.getNodeText(child));
        } else if (child.type === 'namespace_import') {
          // For namespace imports like * as name
          specifiers.push(this.getNodeText(child));
        } else if (child.type === 'named_imports') {
          // Look inside named_imports for specifiers
          for (const namedChild of child.children) {
            if (namedChild.type === 'import_specifier') {
              const nameNode = namedChild.childForFieldName('name');
              if (nameNode) {
                specifiers.push(this.getNodeText(nameNode));
              }
            }
          }
        }
      }
    }

    return specifiers;
  }

  private extractExportedName(node: Parser.SyntaxNode): string {
    // Handle different export patterns
    for (const child of node.children) {
      // Direct exports: export const Component = ..., export function foo() {}, export class Bar {}
      if (child.type === 'variable_declaration' || child.type === 'lexical_declaration') {
        // export const/let/var name = ...
        const declarator = child.children.find(c => c.type === 'variable_declarator');
        if (declarator) {
          const nameNode = declarator.childForFieldName('name');
          if (nameNode) {
            return this.getNodeText(nameNode);
          }
        }
      } else if (child.type === 'class_declaration' || child.type === 'function_declaration') {
        const nameNode = child.childForFieldName('name');
        if (nameNode) {
          return this.getNodeText(nameNode);
        }
      } else if (child.type === 'identifier') {
        // Simple export: export identifier
        return this.getNodeText(child);
      } else if (child.type === 'export_clause') {
        // Handle export { default as Component } patterns
        for (const clauseChild of child.children) {
          if (clauseChild.type === 'export_specifier') {
            // Check for "as" alias pattern
            const children = clauseChild.children;
            let foundAs = false;
            for (let i = 0; i < children.length; i++) {
              if (children[i].text === 'as' && i + 1 < children.length) {
                foundAs = true;
                return this.getNodeText(children[i + 1]); // Return identifier after "as"
              }
            }
            // If no "as", return the export name
            if (!foundAs) {
              const nameNode = clauseChild.children.find(c => c.type === 'identifier');
              if (nameNode) {
                return this.getNodeText(nameNode);
              }
            }
          }
        }
      } else if (child.type === 'export_specifier') {
        // Named export specifier (direct child)
        const nameNode = child.childForFieldName('name');
        if (nameNode) {
          return this.getNodeText(nameNode);
        }
      }
    }

    // Look for default exports
    if (this.isDefaultExport(node)) {
      return 'default';
    }

    return 'unknown';
  }

  // Relationship extraction methods (similar to TypeScript extractor)
  private extractCallRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const functionNode = node.childForFieldName('function');
    if (functionNode) {
      const functionName = this.getNodeText(functionNode);
      const calledSymbol = symbols.find(s => s.name === functionName);

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

  private extractExtendsRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const parent = node.parent;
    if (parent?.type === 'class_declaration') {
      const className = this.getFieldText(parent, 'name');
      const classSymbol = symbols.find(s => s.name === className);

      for (const child of node.children) {
        if (child.type === 'identifier') {
          const superClassName = this.getNodeText(child);
          const superClassSymbol = symbols.find(s => s.name === superClassName);

          if (classSymbol && superClassSymbol) {
            relationships.push(this.createRelationship(
              classSymbol.id,
              superClassSymbol.id,
              RelationshipKind.Extends,
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

  private extractVisibility(node: Parser.SyntaxNode): string {
    // JavaScript doesn't have explicit visibility modifiers like TypeScript
    // But we can infer from naming conventions
    const nameNode = node.childForFieldName('name') || node.childForFieldName('property');
    if (nameNode) {
      const name = this.getNodeText(nameNode);
      if (name.startsWith('#')) return 'private';
      if (name.startsWith('_')) return 'protected'; // Convention
    }
    return 'public';
  }

  // CommonJS require detection
  private isRequireCall(node: Parser.SyntaxNode): boolean {
    if (node.type === 'call_expression') {
      const functionNode = node.childForFieldName('function');
      return functionNode ? this.getNodeText(functionNode) === 'require' : false;
    }
    return false;
  }

  private extractRequireSource(node: Parser.SyntaxNode): string {
    if (node.type === 'call_expression') {
      const args = node.childForFieldName('arguments');
      if (args && args.children.length > 0) {
        const firstArg = args.children.find(c => c.type === 'string');
        if (firstArg) {
          return this.getNodeText(firstArg).replace(/['"`]/g, '');
        }
      }
    }
    return 'unknown';
  }

  private extractDestructuringVariables(node: Parser.SyntaxNode, parentId?: string): Symbol[] {
    const nameNode = node.childForFieldName('name');
    const valueNode = node.childForFieldName('value');
    const symbols: Symbol[] = [];

    if (!nameNode) return symbols;

    const declarationType = this.getDeclarationType(node);
    const valueText = valueNode ? this.getNodeText(valueNode) : '';

    if (nameNode.type === 'object_pattern') {
      // Handle object destructuring: const { name, age, ...rest } = user
      for (const child of nameNode.children) {
        if (child.type === 'shorthand_property_identifier_pattern' ||
            child.type === 'property_identifier' ||
            child.type === 'identifier') {
          const varName = this.getNodeText(child);
          const signature = `${declarationType} { ${varName} } = ${valueText}`;

          symbols.push(this.createSymbol(node, varName, SymbolKind.Variable, {
            signature,
            visibility: this.extractVisibility(node),
            parentId,
            metadata: {
              declarationType,
              isDestructured: true,
              destructuringType: 'object'
            }
          }));
        } else if (child.type === 'rest_pattern') {
          // Handle rest parameters: const { name, ...rest } = user
          const restIdentifier = child.children.find(c => c.type === 'identifier');
          if (restIdentifier) {
            const varName = this.getNodeText(restIdentifier);
            const signature = `${declarationType} { ...${varName} } = ${valueText}`;

            symbols.push(this.createSymbol(node, varName, SymbolKind.Variable, {
              signature,
              visibility: this.extractVisibility(node),
              parentId,
              metadata: {
                declarationType,
                isDestructured: true,
                destructuringType: 'object',
                isRestParameter: true
              }
            }));
          }
        }
      }
    } else if (nameNode.type === 'array_pattern') {
      // Handle array destructuring: const [first, second] = array
      let index = 0;
      for (const child of nameNode.children) {
        if (child.type === 'identifier') {
          const varName = this.getNodeText(child);
          const signature = `${declarationType} [${varName}] = ${valueText}`;

          symbols.push(this.createSymbol(node, varName, SymbolKind.Variable, {
            signature,
            visibility: this.extractVisibility(node),
            parentId,
            metadata: {
              declarationType,
              isDestructured: true,
              destructuringType: 'array',
              destructuringIndex: index
            }
          }));
          index++;
        }
      }
    }

    return symbols;
  }

  private extractRequireDestructuring(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const valueNode = node.childForFieldName('value');

    if (!nameNode || !valueNode || nameNode.type !== 'object_pattern') {
      // Fallback to regular variable extraction
      const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
      return this.createSymbol(node, name, SymbolKind.Variable, {
        signature: this.buildVariableSignature(node, name),
        parentId
      });
    }

    const source = this.extractRequireSource(valueNode);

    // Extract the first destructured property to create a representative symbol
    // In practice, each property should probably be its own symbol, but for now
    // we'll create one symbol representing the destructuring
    for (const child of nameNode.children) {
      if (child.type === 'shorthand_property_identifier_pattern' ||
          child.type === 'property_identifier') {
        const propertyName = this.getNodeText(child);

        return this.createSymbol(node, propertyName, SymbolKind.Import, {
          signature: this.buildVariableSignature(node, this.getNodeText(nameNode)),
          visibility: this.extractVisibility(node),
          parentId,
          metadata: {
            source,
            isCommonJS: true,
            isDestructured: true,
            destructuredProperty: propertyName
          }
        });
      }
    }

    // Fallback
    return this.createSymbol(node, 'destructured', SymbolKind.Import, {
      signature: this.buildVariableSignature(node, this.getNodeText(nameNode)),
      parentId,
      metadata: {
        source,
        isCommonJS: true,
        isDestructured: true
      }
    });
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      } else if (symbol.metadata?.returnType) {
        types.set(symbol.id, symbol.metadata.returnType);
      } else if (symbol.kind === SymbolKind.Function || symbol.kind === SymbolKind.Method) {
        types.set(symbol.id, 'function');
      } else if (symbol.kind === SymbolKind.Class) {
        types.set(symbol.id, 'class');
      } else if (symbol.kind === SymbolKind.Variable) {
        types.set(symbol.id, 'any');
      }
    }
    return types;
  }
}