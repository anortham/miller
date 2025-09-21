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
        case 'razor_inject_directive':
        case 'razor_using_directive':
        case 'razor_page_directive':
        case 'razor_namespace_directive':
        case 'razor_model_directive':
        case 'razor_attribute_directive':
        case 'razor_inherits_directive':
        case 'razor_implements_directive':
          symbol = this.extractDirective(node, parentId);
          break;
        case 'razor_section':
          symbol = this.extractSection(node, parentId);
          break;
        case 'razor_block':
          symbol = this.extractCodeBlock(node, parentId);
          // Also extract C# symbols from within the block
          this.extractCSharpSymbols(node, symbols, symbol?.id || parentId);
          break;
        case 'razor_expression':
          symbol = this.extractExpression(node, parentId);
          break;
        case 'html_element':
        case 'element':
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
        case 'assignment_expression':
          symbol = this.extractAssignment(node, parentId);
          break;
        case 'razor_html_attribute':
          symbol = this.extractHtmlAttribute(node, parentId);
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

    // For certain directives, use the value as the symbol name instead of the directive
    let symbolName = `@${directiveName}`;
    if (directiveName === 'using' && directiveValue) {
      symbolName = directiveValue.trim();
    } else if (directiveName === 'inject' && directiveValue) {
      // Extract property name from "@inject IService PropertyName"
      const parts = directiveValue.trim().split(/\s+/);
      if (parts.length >= 2) {
        symbolName = parts[parts.length - 1]; // Last part is the property name
      }
    }
    // @model, @attribute, @page keep their directive names

    return this.createSymbol(node, symbolName, symbolKind, {
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

    const symbol = this.createSymbol(node, `${blockType}Block`, SymbolKind.Function, {
      signature: `@{ ${content.substring(0, 50)}... }`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-code-block',
        blockType,
        content: content.substring(0, 200)
      }
    });

    return symbol;
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
    const visitNode = (node: Parser.SyntaxNode) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'local_declaration_statement':
          symbol = this.extractLocalVariable(node, parentId);
          break;
        case 'method_declaration':
          symbol = this.extractMethod(node, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'variable_declaration':
          symbol = this.extractVariableDeclaration(node, parentId);
          break;
        case 'assignment_expression':
          symbol = this.extractAssignment(node, parentId);
          break;
      }

      if (symbol) {
        symbols.push(symbol);
      }

      // Recursively visit children
      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(node);
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

  private extractLocalVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle local_declaration_statement -> variable_declaration -> variable_declarator
    let varDeclaration = node;
    if (node.type === 'local_declaration_statement') {
      varDeclaration = node.children.find(c => c.type === 'variable_declaration');
      if (!varDeclaration) return null;
    }

    const declarator = varDeclaration.children.find(c => c.type === 'variable_declarator');
    if (!declarator) return null;

    const nameNode = declarator.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownVariable';

    // Extract type (could be 'var', 'implicit_type', or specific type)
    const typeNode = varDeclaration.children.find(c =>
      c.type === 'implicit_type' || c.type === 'predefined_type' || c.type === 'identifier'
    );
    const type = typeNode ? this.getNodeText(typeNode) : 'var';

    // Extract the initializer/assignment for the signature
    const equalsIndex = varDeclaration.children.findIndex(c => c.type === '=');
    let signature = `${type} ${name}`;
    if (equalsIndex >= 0 && equalsIndex + 1 < varDeclaration.children.length) {
      const initializerNode = varDeclaration.children[equalsIndex + 1];
      const initializer = this.getNodeText(initializerNode);
      signature = `${type} ${name} = ${initializer}`;
    } else {
      // Fallback: try to extract from the full declaration text
      const fullText = this.getNodeText(varDeclaration);
      const match = fullText.match(/=\s*(.+)$/);
      if (match) {
        signature = `${type} ${name} = ${match[1]}`;
      }
    }

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

  private extractVariableDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
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
        type: 'variable-declaration',
        variableType: type
      }
    });
  }

  private extractAssignment(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for assignment patterns like "var name = value"
    const leftSide = node.children[0];
    if (!leftSide) return null;

    let name: string;
    if (leftSide.type === 'identifier') {
      name = this.getNodeText(leftSide);
    } else if (leftSide.type === 'element_access_expression') {
      // Handle ViewData["Title"] = value
      const expression = this.getNodeText(leftSide);
      return this.createSymbol(node, expression, SymbolKind.Variable, {
        signature: `${expression} = ...`,
        visibility: 'private',
        parentId,
        metadata: {
          type: 'assignment',
          expression
        }
      });
    } else {
      return null;
    }

    const rightSide = node.children[2]; // Skip the = operator
    const value = rightSide ? this.getNodeText(rightSide) : '';

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature: `var ${name} = ${value.substring(0, 30)}${value.length > 30 ? '...' : ''}`,
      visibility: 'private',
      parentId,
      metadata: {
        type: 'assignment',
        value: value.substring(0, 100)
      }
    });
  }

  private extractDirectiveName(node: Parser.SyntaxNode): string {
    // Extract directive name from node type or text
    switch (node.type) {
      case 'razor_page_directive':
        return 'page';
      case 'razor_model_directive':
        return 'model';
      case 'razor_using_directive':
        return 'using';
      case 'razor_inject_directive':
        return 'inject';
      case 'razor_attribute_directive':
        return 'attribute';
      case 'razor_namespace_directive':
        return 'namespace';
      case 'razor_inherits_directive':
        return 'inherits';
      case 'razor_implements_directive':
        return 'implements';
      default:
        // Fallback to text parsing
        const text = this.getNodeText(node);
        const match = text.match(/@(\w+)/);
        return match ? match[1] : 'unknown';
    }
  }

  private extractDirectiveValue(node: Parser.SyntaxNode): string | null {
    // Extract directive value from structured AST nodes
    switch (node.type) {
      case 'razor_page_directive':
        const stringLiteral = node.children.find(c => c.type === 'string_literal');
        return stringLiteral ? this.getNodeText(stringLiteral) : null;

      case 'razor_model_directive':
        const identifier = node.children.find(c => c.type === 'identifier');
        return identifier ? this.getNodeText(identifier) : null;

      case 'razor_using_directive':
        const qualifiedName = node.children.find(c => c.type === 'qualified_name' || c.type === 'identifier');
        return qualifiedName ? this.getNodeText(qualifiedName) : null;

      case 'razor_inject_directive':
        const varDeclaration = node.children.find(c => c.type === 'variable_declaration');
        return varDeclaration ? this.getNodeText(varDeclaration) : null;

      case 'razor_attribute_directive':
        const attributeList = node.children.find(c => c.type === 'attribute_list');
        return attributeList ? this.getNodeText(attributeList) : null;

      case 'razor_namespace_directive':
        const namespaceQualified = node.children.find(c => c.type === 'qualified_name' || c.type === 'identifier');
        return namespaceQualified ? this.getNodeText(namespaceQualified) : null;

      case 'razor_inherits_directive':
        const inheritsIdentifier = node.children.find(c => c.type === 'identifier');
        return inheritsIdentifier ? this.getNodeText(inheritsIdentifier) : null;

      case 'razor_implements_directive':
        const implementsIdentifier = node.children.find(c => c.type === 'identifier');
        return implementsIdentifier ? this.getNodeText(implementsIdentifier) : null;

      default:
        // Fallback to text parsing
        const text = this.getNodeText(node);
        const match = text.match(/@\w+\s+(.*)/);
        return match ? match[1].trim() : null;
    }
  }

  private getDirectiveSymbolKind(directiveName: string): SymbolKind {
    switch (directiveName.toLowerCase()) {
      case 'model':
      case 'layout':
        return SymbolKind.Class;
      case 'page':
      case 'using':
      case 'namespace':
        return SymbolKind.Import;
      case 'inherits':
      case 'implements':
        return SymbolKind.Interface;
      case 'inject':
      case 'attribute':
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

  private extractSection(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find the section name from the identifier node
    const identifierNode = this.findChildByType(node, 'identifier');
    if (!identifierNode) return null;

    const sectionName = this.getNodeText(identifierNode);
    const signature = `@section ${sectionName}`;

    return this.createSymbol(node, sectionName, SymbolKind.Module, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-section',
        sectionName
      }
    });
  }

  private extractAssignment(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Check if this is ViewData or ViewBag assignment
    const assignmentText = this.getNodeText(node);

    if (assignmentText.includes('ViewData[') || assignmentText.includes('ViewBag.')) {
      // Extract the variable name for ViewData["Title"] or ViewBag.MetaDescription
      let variableName = 'assignment';

      if (assignmentText.includes('ViewData[')) {
        const match = assignmentText.match(/ViewData\["([^"]+)"\]/);
        if (match) {
          variableName = `ViewData_${match[1]}`;
        }
      } else if (assignmentText.includes('ViewBag.')) {
        const match = assignmentText.match(/ViewBag\.(\w+)/);
        if (match) {
          variableName = `ViewBag_${match[1]}`;
        }
      }

      return this.createSymbol(node, variableName, SymbolKind.Variable, {
        signature: assignmentText,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-assignment',
          isViewData: assignmentText.includes('ViewData'),
          isViewBag: assignmentText.includes('ViewBag')
        }
      });
    }

    return null;
  }

  private extractHtmlAttribute(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Check for data binding attributes like @bind-Value
    const attributeText = this.getNodeText(node);

    if (attributeText.includes('@bind')) {
      const bindingName = attributeText.includes('-Value') ? 'bind_Value' : 'bind';
      const signature = attributeText;

      return this.createSymbol(node, bindingName, SymbolKind.Variable, {
        signature,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-binding',
          isDataBinding: true
        }
      });
    }

    return null;
  }

  private extractTokenDirective(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle token-based directives like at_inherits, at_namespace, at_implements
    const directiveType = node.type.replace('at_', '');
    let directiveName = `@${directiveType}`;

    // Look for the next sibling to get the directive value
    let directiveValue = '';
    let current = node.nextSibling;

    // Skip whitespace and find the value
    while (current && (current.type === ' ' || current.type === '\t' || current.type === '\n')) {
      current = current.nextSibling;
    }

    if (current && (current.type === 'identifier' || current.type === 'qualified_name')) {
      directiveValue = this.getNodeText(current);
    }

    const signature = directiveValue ? `${directiveName} ${directiveValue}` : directiveName;

    return this.createSymbol(node, directiveName, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-token-directive',
        directiveType,
        directiveValue
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
      } else if (symbol.metadata?.isDataBinding) {
        // Improve type inference for properties
        types.set(symbol.id, 'bool');
      }
    }
    return types;
  }
}