import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, SymbolKind, Relationship, RelationshipKind } from './base-extractor.js';

export class LuaExtractor extends BaseExtractor {
  private symbols: Symbol[] = [];
  private relationships: Relationship[] = [];

  constructor(language: string, filePath: string, content: string) {
    super(language, filePath, content);
  }

  extractSymbols(tree: Parser.Tree): Symbol[] {
    this.symbols = [];
    this.relationships = [];

    if (tree && tree.rootNode) {
      this.traverseNode(tree.rootNode, null);
    }

    // Post-process to detect Lua class patterns
    this.detectLuaClasses();

    return this.symbols;
  }

  protected traverseNode(node: Parser.SyntaxNode, parentId: string | null): void {
    let symbol: Symbol | null = null;

    switch (node.type) {
      case 'function_definition_statement':
        symbol = this.extractFunctionDefinitionStatement(node, parentId);
        break;
      case 'local_function_definition_statement':
        symbol = this.extractLocalFunctionDefinitionStatement(node, parentId);
        break;
      case 'local_variable_declaration':
        symbol = this.extractLocalVariableDeclaration(node, parentId);
        break;
      case 'assignment_statement':
        symbol = this.extractAssignmentStatement(node, parentId);
        break;
      case 'variable_assignment':
        symbol = this.extractVariableAssignment(node, parentId);
        break;
      case 'table':
        // Only extract table fields if not already handled by variable assignment
        // (we handle tables in extractLocalVariableDeclaration and extractVariableAssignment)
        if (!this.isTableHandledByParent(node)) {
          this.extractTableFields(node, parentId);
        }
        break;
      default:
        // Continue traversing
        break;
    }

    // Traverse children with current symbol as parent
    const currentParentId = symbol?.id || parentId;
    for (const child of node.children) {
      this.traverseNode(child, currentParentId);
    }
  }

  private extractFunctionDefinitionStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // Handle both regular functions and colon syntax methods
    let nameNode = this.findChildByType(node, 'identifier');
    let name: string;
    let kind = SymbolKind.Function;
    let methodParentId = parentId;

    if (!nameNode) {
      // Check for colon syntax: function obj:method() or dot syntax: function obj.method()
      const variableNode = this.findChildByType(node, 'variable');
      if (variableNode) {
        const fullName = this.getNodeText(variableNode);

        // Handle colon syntax: function obj:method()
        if (fullName.includes(':')) {
          const parts = fullName.split(':');
          if (parts.length === 2) {
            const [objectName, methodName] = parts;
            name = methodName;
            nameNode = variableNode; // Use variable node for positioning
            kind = SymbolKind.Method;

            // Try to find the object this method belongs to
            const objectSymbol = this.symbols.find(s => s.name === objectName);
            if (objectSymbol) {
              methodParentId = objectSymbol.id;
            }
          } else {
            return null;
          }
        }
        // Handle dot syntax: function obj.method()
        else if (fullName.includes('.')) {
          const parts = fullName.split('.');
          if (parts.length === 2) {
            const [objectName, methodName] = parts;
            name = methodName;
            nameNode = variableNode; // Use variable node for positioning
            kind = SymbolKind.Method;

            // Try to find the object this method belongs to
            const objectSymbol = this.symbols.find(s => s.name === objectName);
            if (objectSymbol) {
              methodParentId = objectSymbol.id;
            }
          } else {
            return null;
          }
        }
        else {
          return null;
        }
      } else {
        return null;
      }
    } else {
      name = this.getNodeText(nameNode);
    }

    const signature = this.getNodeText(node);

    // Determine visibility: underscore prefix indicates private
    const visibility = name.startsWith('_') ? 'private' : 'public';

    const symbol = this.createSymbol(nameNode, name, kind, {
      signature,
      parentId: methodParentId,
      visibility
    });

    this.symbols.push(symbol);
    return symbol;
  }

  private extractLocalFunctionDefinitionStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(nameNode, name, SymbolKind.Function, {
      signature,
      parentId,
      visibility: 'private'
    });

    this.symbols.push(symbol);
    return symbol;
  }

  private extractLocalVariableDeclaration(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const variableList = this.findChildByType(node, 'variable_list');
    const expressionList = this.findChildByType(node, 'expression_list');

    if (!variableList) return null;

    const signature = this.getNodeText(node);
    const variables = variableList.children.filter(child =>
      child.type === 'variable' || child.type === 'identifier'
    );

    // Get the corresponding expressions if they exist
    const expressions = expressionList ? expressionList.children.filter(child =>
      child.type !== ',' // Filter out commas
    ) : [];

    // Create symbols for each local variable
    for (let i = 0; i < variables.length; i++) {
      const varNode = variables[i];
      let nameNode: Parser.SyntaxNode | null = null;

      if (varNode.type === 'identifier') {
        nameNode = varNode;
      } else if (varNode.type === 'variable') {
        nameNode = this.findChildByType(varNode, 'identifier');
      }

      if (nameNode) {
        const name = this.getNodeText(nameNode);

        // Check if the corresponding expression is a function or import
        const expression = expressions[i];
        let kind = SymbolKind.Variable;
        let dataType = 'unknown';

        if (expression) {
          if (expression.type === 'function_definition') {
            kind = SymbolKind.Function;
            dataType = 'function';
          } else {
            dataType = this.inferTypeFromExpression(expression);
            if (dataType === 'import') {
              kind = SymbolKind.Import;
            }
          }
        }

        const symbol = this.createSymbol(nameNode, name, kind, {
          signature,
          parentId,
          visibility: 'private',
          metadata: { dataType }
        });

        // Set dataType as direct property for tests
        (symbol as any).dataType = dataType;

        this.symbols.push(symbol);

        // If this is a table, extract its fields with this symbol as parent
        if (expression && expression.type === 'table') {
          this.extractTableFields(expression, symbol.id);
        }
      }
    }

    return null;
  }

  private inferTypeFromExpression(node: Parser.SyntaxNode): string {
    switch (node.type) {
      case 'string':
        return 'string';
      case 'number':
        return 'number';
      case 'true':
      case 'false':
        return 'boolean';
      case 'nil':
        return 'nil';
      case 'function_definition':
        return 'function';
      case 'table_constructor':
      case 'table':
        return 'table';
      case 'call':
        // Check if this is a require() call
        const callee = this.findChildByType(node, 'variable');
        if (callee) {
          const identifier = this.findChildByType(callee, 'identifier');
          if (identifier && this.getNodeText(identifier) === 'require') {
            return 'import';
          }
        }
        return 'unknown';
      default:
        return 'unknown';
    }
  }

  private extractAssignmentStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // Get the left and right sides of the assignment
    const children = node.children;
    if (children.length < 3) return null; // Need at least: left = right

    const left = children[0];
    const right = children[2]; // Skip the '=' operator

    // Handle variable_list assignments (e.g., local a, b = 1, 2)
    if (left.type === 'variable_list') {
      const variables = left.children.filter(child => child.type === 'variable');

      for (let i = 0; i < variables.length; i++) {
        const varNode = variables[i];
        const nameNode = this.findChildByType(varNode, 'identifier');

        if (nameNode) {
          const name = this.getNodeText(nameNode);
          const signature = this.getNodeText(node);

          // Determine kind and type based on the assignment
          let kind = SymbolKind.Variable;
          let dataType = 'unknown';

          if (right.type === 'expression_list' && right.children[i]) {
            const expression = right.children[i];
            if (expression.type === 'function_definition') {
              kind = SymbolKind.Function;
              dataType = 'function';
            } else {
              dataType = this.inferTypeFromExpression(expression);
            }
          } else if (right.type === 'function_definition') {
            kind = SymbolKind.Function;
            dataType = 'function';
          } else {
            dataType = this.inferTypeFromExpression(right);
          }

          const symbol = this.createSymbol(nameNode, name, kind, {
            signature,
            parentId,
            visibility: 'public',
            metadata: { dataType }
          });

          // Set dataType as direct property for tests
          (symbol as any).dataType = dataType;

          this.symbols.push(symbol);
        }
      }
    }
    // Handle simple identifier assignments (e.g., PI = 3.14159) and dot notation (e.g., M.PI = 3.14159)
    else if (left.type === 'variable') {
      const fullVariableName = this.getNodeText(left);

      // Handle dot notation assignments: M.PI = 3.14159
      if (fullVariableName.includes('.')) {
        const parts = fullVariableName.split('.');
        if (parts.length === 2) {
          const [objectName, propertyName] = parts;
          const signature = this.getNodeText(node);

          // Determine kind and type based on the assignment
          let kind = SymbolKind.Variable;
          let dataType = 'unknown';

          if (right.type === 'function_definition') {
            kind = SymbolKind.Function;
            dataType = 'function';
          } else {
            dataType = this.inferTypeFromExpression(right);
          }

          // Find the object this property belongs to
          const objectSymbol = this.symbols.find(s => s.name === objectName);
          const propertyParentId = objectSymbol ? objectSymbol.id : parentId;

          const symbol = this.createSymbol(left, propertyName, kind, {
            signature,
            parentId: propertyParentId,
            visibility: 'public',
            metadata: { dataType }
          });

          // Set dataType as direct property for tests
          (symbol as any).dataType = dataType;

          this.symbols.push(symbol);
        }
      }
      // Handle simple identifier assignments: PI = 3.14159
      else {
        const nameNode = this.findChildByType(left, 'identifier');

        if (nameNode) {
          const name = this.getNodeText(nameNode);
          const signature = this.getNodeText(node);

          // Determine kind and type based on the assignment
          let kind = SymbolKind.Variable;
          let dataType = 'unknown';

          if (right.type === 'function_definition') {
            kind = SymbolKind.Function;
            dataType = 'function';
          } else {
            dataType = this.inferTypeFromExpression(right);
          }

          const symbol = this.createSymbol(nameNode, name, kind, {
            signature,
            parentId,
            visibility: 'public', // Global assignments are public
            metadata: { dataType }
          });

          // Set dataType as direct property for tests
          (symbol as any).dataType = dataType;

          this.symbols.push(symbol);
        }
      }
    }

    return null;
  }

  private extractVariableAssignment(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // Extract global variable assignments like: PI = 3.14159
    const variableList = this.findChildByType(node, 'variable_list');
    const expressionList = this.findChildByType(node, 'expression_list');

    if (!variableList) return null;

    const signature = this.getNodeText(node);
    const variables = variableList.children.filter(child => child.type === 'variable');
    const expressions = expressionList ? expressionList.children.filter(child =>
      child.type !== ',' // Filter out commas
    ) : [];

    // Create symbols for each variable
    for (let i = 0; i < variables.length; i++) {
      const varNode = variables[i];
      const fullVariableName = this.getNodeText(varNode);

      // Handle dot notation: M.PI = 3.14159
      if (fullVariableName.includes('.')) {
        const parts = fullVariableName.split('.');
        if (parts.length === 2) {
          const [objectName, propertyName] = parts;

          // Determine kind and type based on the assignment
          // Module properties (M.PI) should be classified as Field
          let kind = SymbolKind.Field;
          let dataType = 'unknown';

          const expression = expressions[i];
          if (expression) {
            if (expression.type === 'function_definition') {
              kind = SymbolKind.Method; // Module methods should be Method, not Function
              dataType = 'function';
            } else {
              dataType = this.inferTypeFromExpression(expression);
            }
          }

          // Find the object this property belongs to
          const objectSymbol = this.symbols.find(s => s.name === objectName);
          const propertyParentId = objectSymbol ? objectSymbol.id : parentId;

          const symbol = this.createSymbol(varNode, propertyName, kind, {
            signature,
            parentId: propertyParentId,
            visibility: 'public',
            metadata: { dataType }
          });

          // Set dataType as direct property for tests
          (symbol as any).dataType = dataType;

          this.symbols.push(symbol);

          // If this is a table, extract its fields with this symbol as parent
          if (expression && expression.type === 'table') {
            this.extractTableFields(expression, symbol.id);
          }
        }
      }
      // Handle simple variable: PI = 3.14159
      else {
        const nameNode = this.findChildByType(varNode, 'identifier');

        if (nameNode) {
          const name = this.getNodeText(nameNode);

          // Determine kind and type based on the assignment
          let kind = SymbolKind.Variable;
          let dataType = 'unknown';

          const expression = expressions[i];
          if (expression) {
            if (expression.type === 'function_definition') {
              kind = SymbolKind.Function;
              dataType = 'function';
            } else {
              dataType = this.inferTypeFromExpression(expression);
            }
          }

          const symbol = this.createSymbol(nameNode, name, kind, {
            signature,
            parentId,
            visibility: 'public', // Global variables are public
            metadata: { dataType }
          });

          // Set dataType as direct property for tests
          (symbol as any).dataType = dataType;

          this.symbols.push(symbol);

          // If this is a table, extract its fields with this symbol as parent
          if (expression && expression.type === 'table') {
            this.extractTableFields(expression, symbol.id);
          }
        }
      }
    }

    return null;
  }

  private extractTableFields(node: Parser.SyntaxNode, parentId: string | null): void {
    // Extract fields from table constructor: { field = value, method = function() end }
    const fieldList = this.findChildByType(node, 'field_list');
    if (!fieldList) return;

    for (const child of fieldList.children) {
      if (child.type === 'field') {
        this.extractTableField(child, parentId);
      }
    }
  }

  private extractTableField(node: Parser.SyntaxNode, parentId: string | null): void {
    // Handle field definitions like: field = value or field = function() end
    const children = node.children;
    if (children.length < 3) return; // Need at least: name = value

    const nameNode = children[0]; // field name
    const equalNode = children[1]; // '=' operator
    const valueNode = children[2]; // field value

    if (equalNode.type !== '=' || nameNode.type !== 'identifier') return;

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(node);

    // Determine if this is a method (function) or field (value)
    let kind = SymbolKind.Field;
    let dataType = 'unknown';

    if (valueNode.type === 'function_definition') {
      kind = SymbolKind.Method;
      dataType = 'function';
    } else {
      dataType = this.inferTypeFromExpression(valueNode);
    }

    const symbol = this.createSymbol(nameNode, name, kind, {
      signature,
      parentId,
      visibility: 'public',
      metadata: { dataType }
    });

    // Set dataType as direct property for tests
    (symbol as any).dataType = dataType;

    this.symbols.push(symbol);
  }

  private isTableHandledByParent(node: Parser.SyntaxNode): boolean {
    // Check if this table is part of a variable assignment
    // Look for patterns: local var = { ... } or var = { ... }
    const parent = node.parent;
    if (!parent) return false;

    // Check if parent is expression_list and grandparent is local_variable_declaration
    if (parent.type === 'expression_list') {
      const grandparent = parent.parent;
      if (grandparent && (grandparent.type === 'local_variable_declaration' || grandparent.type === 'variable_assignment')) {
        return true;
      }
    }

    return false;
  }

  private detectLuaClasses(): void {
    // Post-process all symbols to detect Lua class patterns
    for (const symbol of this.symbols) {
      if (symbol.kind === SymbolKind.Variable) {
        const className = symbol.name;

        // Pattern 1: Tables with metatable setup (local Class = {})
        const isTable = symbol.dataType === 'table';

        // Pattern 2: Variables created with setmetatable (local Dog = setmetatable({}, Animal))
        const isSetmetatable = symbol.signature?.includes('setmetatable(');

        // Only check class patterns for tables or setmetatable creations
        if (isTable || isSetmetatable) {
          // Look for metatable patterns that indicate this is a class
          const hasIndexPattern = this.symbols.some(s =>
            s.signature?.includes(`${className}.__index = ${className}`)
          );

          const hasNewMethod = this.symbols.some(s =>
            s.name === 'new' && s.signature?.includes(`${className}.new`)
          );

          const hasColonMethods = this.symbols.some(s =>
            s.kind === SymbolKind.Method && s.signature?.includes(`${className}:`)
          );

          // If it has metatable patterns, upgrade to Class
          if (hasIndexPattern || (hasNewMethod && hasColonMethods) || isSetmetatable) {
            symbol.kind = SymbolKind.Class;

            // Extract inheritance information from setmetatable pattern
            if (isSetmetatable && symbol.signature) {
              const setmetatableMatch = symbol.signature.match(/setmetatable\(\s*\{\s*\}\s*,\s*(\w+)\s*\)/);
              if (setmetatableMatch) {
                const parentClassName = setmetatableMatch[1];
                // Verify the parent class exists in our symbols
                const parentClass = this.symbols.find(s =>
                  s.name === parentClassName &&
                  (s.kind === SymbolKind.Class || s.dataType === 'table')
                );
                if (parentClass) {
                  (symbol as any).baseClass = parentClassName;
                }
              }
            }
          }
        }
      }
    }
  }

  getRelationships(): Relationship[] {
    return this.relationships;
  }
}