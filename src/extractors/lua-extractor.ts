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
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      parentId,
      visibility: 'public'
    });

    this.symbols.push(symbol);
    return symbol;
  }

  private extractLocalFunctionDefinitionStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(node, name, SymbolKind.Function, {
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

        // Check if the corresponding expression is a function
        const expression = expressions[i];
        let kind = SymbolKind.Variable;
        let dataType = 'unknown';

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
          visibility: 'private',
          metadata: { dataType }
        });

        this.symbols.push(symbol);
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
        return 'table';
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

    // For now, handle simple variable assignments
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

          this.symbols.push(symbol);
        }
      }
    }

    return null;
  }

  getRelationships(): Relationship[] {
    return this.relationships;
  }
}