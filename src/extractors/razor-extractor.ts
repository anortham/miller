import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class RazorExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'razor_directive':
          symbol = this.extractDirective(node, parentId);
          break;
        case 'razor_block':
          symbol = this.extractCodeBlock(node, parentId);
          break;
        case 'razor_expression':
          symbol = this.extractExpression(node, parentId);
          break;
        case 'html_element':
          symbol = this.extractHtmlElement(node, parentId);
          break;
        case 'razor_component':
          symbol = this.extractComponent(node, parentId);
          break;
        case 'csharp_code':
          this.extractCSharpSymbols(node, symbols, parentId);
          break;
        case 'using_directive':
          symbol = this.extractUsing(node, parentId);
          break;
        case 'namespace_declaration':
          symbol = this.extractNamespace(node, parentId);
          break;
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'method_declaration':
          symbol = this.extractMethod(node, parentId);
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
      console.warn('Razor parsing failed:', error);
    }
    return symbols;
  }

  private extractDirective(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const directiveName = this.extractDirectiveName(node);
    const directiveValue = this.extractDirectiveValue(node);

    let signature = `@${directiveName}`;
    if (directiveValue) {
      signature += ` ${directiveValue}`;
    }

    const symbolKind = this.getDirectiveSymbolKind(directiveName);

    return this.createSymbol(node, directiveName, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-directive',
        directiveName,
        directiveValue
      }
    });
  }

  private extractCodeBlock(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const blockType = this.getCodeBlockType(node);
    const content = this.getNodeText(node);

    return this.createSymbol(node, `${blockType}Block`, SymbolKind.Function, {
      signature: `@{ ${content.substring(0, 50)}... }`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-code-block',
        blockType,
        content: content.substring(0, 200)
      }
    });
  }

  private extractExpression(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const expression = this.getNodeText(node);
    const variableName = this.extractVariableFromExpression(expression);

    return this.createSymbol(node, variableName || 'expression', SymbolKind.Variable, {
      signature: `@${expression}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-expression',
        expression
      }
    });
  }

  private extractHtmlElement(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const tagName = this.extractHtmlTagName(node);
    const attributes = this.extractHtmlAttributes(node);

    let signature = `<${tagName}`;
    if (attributes.length > 0) {
      signature += ` ${attributes.join(' ')}`;
    }
    signature += '>';

    return this.createSymbol(node, tagName, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'html-element',
        tagName,
        attributes
      }
    });
  }

  private extractComponent(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const componentName = this.extractComponentName(node);
    const parameters = this.extractComponentParameters(node);

    let signature = `<${componentName}`;
    if (parameters.length > 0) {
      signature += ` ${parameters.join(' ')}`;
    }
    signature += ' />';

    return this.createSymbol(node, componentName, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-component',
        componentName,
        parameters
      }
    });
  }

  private extractCSharpSymbols(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    // For embedded C# code, we'll extract basic constructs
    for (const child of node.children) {
      let symbol: Symbol | null = null;

      switch (child.type) {
        case 'local_declaration_statement':
          symbol = this.extractLocalVariable(child, parentId);
          break;
        case 'method_declaration':
          symbol = this.extractMethod(child, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(child, parentId);
          break;
      }

      if (symbol) {
        symbols.push(symbol);
      }
    }
  }

  private extractUsing(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const namespaceName = this.extractNamespaceName(node);

    return this.createSymbol(node, namespaceName, SymbolKind.Import, {
      signature: `@using ${namespaceName}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'using-directive',
        namespace: namespaceName
      }
    });
  }

  private extractNamespace(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'qualified_name' || c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownNamespace';

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature: `@namespace ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'namespace'
      }
    });
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownClass';

    const modifiers = this.extractModifiers(node);
    let signature = `class ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'class',
        modifiers
      }
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownMethod';

    const modifiers = this.extractModifiers(node);
    const parameters = this.extractMethodParameters(node);
    const returnType = this.extractReturnType(node);

    let signature = `${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (returnType) {
      signature = `${returnType} ${signature}`;
    }

    signature += parameters || '()';

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'method',
        modifiers,
        parameters,
        returnType
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const type = this.extractPropertyType(node);

    let signature = `${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (type) {
      signature = `${type} ${signature}`;
    }

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'property',
        modifiers,
        propertyType: type
      }
    });
  }

  private extractLocalVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const declarator = node.children.find(c => c.type === 'variable_declarator');
    if (!declarator) return null;

    const nameNode = declarator.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownVariable';

    const type = this.extractVariableType(node);

    let signature = `${type || 'var'} ${name}`;

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: 'private',
      parentId,
      metadata: {
        type: 'local-variable',
        variableType: type
      }
    });
  }

  private extractDirectiveName(node: Parser.SyntaxNode): string {
    // Extract directive name from node text
    const text = this.getNodeText(node);
    const match = text.match(/@(\w+)/);
    return match ? match[1] : 'unknown';
  }

  private extractDirectiveValue(node: Parser.SyntaxNode): string | null {
    const text = this.getNodeText(node);
    const match = text.match(/@\w+\s+(.*)/);
    return match ? match[1].trim() : null;
  }

  private getDirectiveSymbolKind(directiveName: string): SymbolKind {
    switch (directiveName.toLowerCase()) {
      case 'model':
      case 'page':
      case 'layout':
        return SymbolKind.Class;
      case 'using':
      case 'namespace':
        return SymbolKind.Import;
      case 'inherits':
      case 'implements':
        return SymbolKind.Interface;
      case 'inject':
        return SymbolKind.Property;
      case 'code':
      case 'functions':
        return SymbolKind.Function;
      default:
        return SymbolKind.Variable;
    }
  }

  private getCodeBlockType(node: Parser.SyntaxNode): string {
    const text = this.getNodeText(node);
    if (text.includes('@code')) return 'code';
    if (text.includes('@functions')) return 'functions';
    if (text.includes('@{')) return 'expression';
    return 'block';
  }

  private extractVariableFromExpression(expression: string): string | null {
    const match = expression.match(/(\w+)/);
    return match ? match[1] : null;
  }

  private extractHtmlTagName(node: Parser.SyntaxNode): string {
    const tagNode = node.children.find(c => c.type === 'tag_name' || c.type === 'identifier');
    return tagNode ? this.getNodeText(tagNode) : 'div';
  }

  private extractHtmlAttributes(node: Parser.SyntaxNode): string[] {
    const attributes: string[] = [];
    for (const child of node.children) {
      if (child.type === 'attribute') {
        attributes.push(this.getNodeText(child));
      }
    }
    return attributes;
  }

  private extractComponentName(node: Parser.SyntaxNode): string {
    const nameNode = node.children.find(c => c.type === 'identifier' || c.type === 'tag_name');
    return nameNode ? this.getNodeText(nameNode) : 'UnknownComponent';
  }

  private extractComponentParameters(node: Parser.SyntaxNode): string[] {
    const parameters: string[] = [];
    for (const child of node.children) {
      if (child.type === 'attribute' || child.type === 'parameter') {
        parameters.push(this.getNodeText(child));
      }
    }
    return parameters;
  }

  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];
    for (const child of node.children) {
      if (['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'sealed'].includes(child.type)) {
        modifiers.push(this.getNodeText(child));
      }
    }
    return modifiers;
  }

  private extractMethodParameters(node: Parser.SyntaxNode): string | null {
    const paramList = node.children.find(c => c.type === 'parameter_list');
    return paramList ? this.getNodeText(paramList) : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    const returnType = node.children.find(c => c.type === 'predefined_type' || c.type === 'identifier');
    return returnType ? this.getNodeText(returnType) : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    const type = node.children.find(c => c.type === 'predefined_type' || c.type === 'identifier');
    return type ? this.getNodeText(type) : null;
  }

  private extractVariableType(node: Parser.SyntaxNode): string | null {
    const type = node.children.find(c => c.type === 'predefined_type' || c.type === 'identifier');
    return type ? this.getNodeText(type) : null;
  }

  private extractNamespaceName(node: Parser.SyntaxNode): string {
    const nameNode = node.children.find(c => c.type === 'qualified_name' || c.type === 'identifier');
    return nameNode ? this.getNodeText(nameNode) : 'UnknownNamespace';
  }

  private determineVisibility(modifiers: string[]): 'public' | 'private' | 'protected' {
    if (modifiers.includes('private')) return 'private';
    if (modifiers.includes('protected')) return 'protected';
    return 'public';
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'razor_component':
          this.extractComponentRelationships(node, symbols, relationships);
          break;
        case 'using_directive':
          this.extractUsingRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractComponentRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const componentName = this.extractComponentName(node);
    const componentSymbol = symbols.find(s => s.name === componentName);

    if (componentSymbol) {
      relationships.push({
        fromSymbolId: `razor-page:${this.filePath}`,
        toSymbolId: componentSymbol.id,
        kind: RelationshipKind.Uses,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { componentName }
      });
    }
  }

  private extractUsingRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const namespace = this.extractNamespaceName(node);

    relationships.push({
      fromSymbolId: `razor-page:${this.filePath}`,
      toSymbolId: `namespace:${namespace}`,
      kind: RelationshipKind.Imports,
      filePath: this.filePath,
      lineNumber: node.startPosition.row + 1,
      confidence: 1.0,
      metadata: { namespace }
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