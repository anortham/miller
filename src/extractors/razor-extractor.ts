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
        case 'razor_addtaghelper_directive':
          symbol = this.extractDirective(node, parentId);
          break;
        case 'at_namespace':
        case 'at_inherits':
        case 'at_implements':
          symbol = this.extractTokenDirective(node, parentId);
          break;
        case 'razor_section':
          symbol = this.extractSection(node, parentId);
          break;
        case 'razor_block':
          symbol = this.extractCodeBlock(node, parentId);
          // Also extract C# symbols from within the block
          this.extractCSharpSymbols(node, symbols, symbol?.id || parentId);
          // Don't visit children of razor_block since we already extracted them
          return;
        case 'razor_expression':
          symbol = this.extractExpression(node, parentId);
          break;
        case 'razor_implicit_expression':
          // Handle @addTagHelper directives that are parsed as implicit expressions
          const implicitText = this.getNodeText(node);
          if (implicitText.trim().startsWith('@addTagHelper')) {
            symbol = this.extractAddTagHelperDirective(node, parentId);
          } else {
            symbol = this.extractExpression(node, parentId);
          }
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
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'field_declaration':
          symbol = this.extractField(node, parentId);
          break;
        case 'assignment_expression':
          symbol = this.extractAssignment(node, parentId);
          break;
        case 'invocation_expression':
          symbol = this.extractInvocation(node, parentId);
          break;
        case 'razor_html_attribute':
          symbol = this.extractHtmlAttribute(node, parentId, symbols);
          break;
        case 'attribute':
          // Handle HTML/Razor attributes like @bind-Value
          symbol = this.extractRazorAttribute(node, parentId);
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

  private extractAddTagHelperDirective(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Handle @addTagHelper directives that are parsed as implicit expressions
    const directiveText = this.getNodeText(node);
    const match = directiveText.match(/@addTagHelper\s+(.+)/);
    const directiveValue = match ? match[1].trim() : '';

    const signature = `@addTagHelper ${directiveValue}`;

    return this.createSymbol(node, '@addTagHelper', SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'razor-directive',
        directiveName: 'addTagHelper',
        directiveValue,
        isAddTagHelper: true
      }
    });
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
        case 'local_function_statement':
          // Methods inside @code blocks are parsed as local functions
          symbol = this.extractLocalFunction(node, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'field_declaration':
          symbol = this.extractField(node, parentId);
          break;
        case 'variable_declaration':
          symbol = this.extractVariableDeclaration(node, parentId);
          break;
        case 'assignment_expression':
          symbol = this.extractAssignment(node, parentId);
          break;
        case 'invocation_expression':
          symbol = this.extractInvocation(node, parentId);
          break;
        case 'element_access_expression':
          // Handle expressions like ViewData["Title"]
          symbol = this.extractElementAccess(node, parentId);
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
    let name = 'unknownMethod';

    // EXPLICIT INTERFACE FIX: Handle explicit interface implementations like "void IDisposable.Dispose()"
    let interfaceQualification = '';
    const explicitImplementation = node.children.find(c => c.type === 'explicit_interface_specifier');
    if (explicitImplementation) {
      // For explicit interface implementations, keep method name but store interface for signature
      const interfaceNode = explicitImplementation.children.find(c => c.type === 'identifier');
      const methodNode = node.children.find(c => c.type === 'identifier' && c !== interfaceNode);

      if (interfaceNode && methodNode) {
        const interfaceName = this.getNodeText(interfaceNode);
        const methodName = this.getNodeText(methodNode);
        name = methodName; // Just the method name for symbol.name
        interfaceQualification = `${interfaceName}.`; // Store interface for signature
      }
    } else {
      // Regular method name extraction
      const nameNode = node.children.find(c => c.type === 'identifier');
      name = nameNode ? this.getNodeText(nameNode) : 'unknownMethod';
    }

    const modifiers = this.extractModifiers(node);
    const parameters = this.extractMethodParameters(node);
    const returnType = this.extractReturnType(node);
    const attributes = this.extractAttributes(node);

    let signature = ``;

    // Build signature in correct order: [attributes] [modifiers] [returnType] [name] [parameters]
    if (attributes.length > 0) {
      signature += `${attributes.join(' ')} `;
    }

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (returnType) {
      signature += `${returnType} `;
    }

    // EXPLICIT INTERFACE FIX: Include interface qualification in signature
    signature += `${interfaceQualification}${name}`;
    signature += parameters || '()';

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'method',
        modifiers,
        parameters,
        returnType,
        attributes
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Find the property name identifier (should be after the type but before accessors)
    let nameNode: Parser.SyntaxNode | undefined;
    for (let i = 0; i < node.children.length; i++) {
      const child = node.children[i];
      if (child.type === 'identifier') {
        // Check if this identifier comes after a type node
        const hasPrecedingType = node.children.slice(0, i).some(c =>
          c.type === 'predefined_type' || c.type === 'nullable_type' ||
          c.type === 'array_type' || c.type === 'generic_name' ||
          (c.type === 'identifier' && node.children.slice(0, i).some(prev => prev.type === 'modifier'))
        );

        if (hasPrecedingType) {
          nameNode = child;
          break;
        }
      }
    }

    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const type = this.extractPropertyType(node);
    const attributes = this.extractAttributes(node);

    let signature = ``;

    // Build signature in correct order: [attributes] [modifiers] [type] [name]
    if (attributes.length > 0) {
      signature += `${attributes.join(' ')} `;
    }

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (type) {
      signature += `${type} `;
    }

    signature += name;

    // Look for property accessor (get; set;)
    const accessorList = node.children.find(c => c.type === 'accessor_list');
    if (accessorList) {
      const accessors = accessorList.children
        .filter(c => c.type === 'get_accessor_declaration' || c.type === 'set_accessor_declaration')
        .map(c => c.type === 'get_accessor_declaration' ? 'get' : 'set');
      if (accessors.length > 0) {
        signature += ` { ${accessors.join('; ')}; }`;
      }
    }

    // Look for property initializer
    const equalsNode = node.children.find(c => c.type === '=');
    if (equalsNode) {
      const initializerIndex = node.children.indexOf(equalsNode) + 1;
      if (initializerIndex < node.children.length) {
        const initializer = this.getNodeText(node.children[initializerIndex]);
        signature += ` = ${initializer}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'property',
        modifiers,
        propertyType: type,
        attributes
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
      const fullAssignment = this.getNodeText(node);

      // Extract variable name for ViewData["Title"]
      let variableName = expression;
      if (expression.includes('ViewData[')) {
        const match = expression.match(/ViewData\["([^"]+)"\]/);
        if (match) {
          variableName = `ViewData_${match[1]}`;
        }
      }

      return this.createSymbol(node, variableName, SymbolKind.Variable, {
        signature: fullAssignment,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-assignment',
          isViewData: true,
          expression
        }
      });
    } else if (leftSide.type === 'member_access_expression') {
      // Handle ViewBag.MetaDescription = value
      const expression = this.getNodeText(leftSide);
      const fullAssignment = this.getNodeText(node);

      if (expression.includes('ViewBag.')) {
        const match = expression.match(/ViewBag\.(\w+)/);
        let variableName = expression;
        if (match) {
          variableName = `ViewBag_${match[1]}`;
        }

        return this.createSymbol(node, variableName, SymbolKind.Variable, {
          signature: fullAssignment,
          visibility: 'public',
          parentId,
          metadata: {
            type: 'razor-assignment',
            isViewBag: true,
            expression
          }
        });
      }
      return null;
    } else {
      return null;
    }

    const rightSide = node.children[2]; // Skip the = operator
    const value = rightSide ? this.getNodeText(rightSide) : '';

    // LAYOUT FIX: Handle layout assignments without 'var' prefix
    let signaturePrefix = 'var ';
    if (name === 'Layout') {
      signaturePrefix = ''; // Layout assignments don't use 'var'
    }

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature: `${signaturePrefix}${name} = ${value.substring(0, 30)}${value.length > 30 ? '...' : ''}`,
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
      case 'razor_addtaghelper_directive':
        return 'addTagHelper';
      default:
        // Fallback to text parsing
        const text = this.getNodeText(node);
        const match = text.match(/@(\w+)/);
        // Special handling for addTagHelper
        if (text.includes('@addTagHelper')) {
          return 'addTagHelper';
        }
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
      case 'razor_addtaghelper_directive':
        // Extract the assembly pattern like "*, Microsoft.AspNetCore.Mvc.TagHelpers"
        const addTagHelperText = this.getNodeText(node);
        const addTagHelperMatch = addTagHelperText.match(/@addTagHelper\s+(.+)/);
        return addTagHelperMatch ? addTagHelperMatch[1].trim() : null;

      default:
        // Fallback to text parsing
        const text = this.getNodeText(node);
        // Special handling for addTagHelper
        if (text.includes('@addTagHelper')) {
          const addTagHelperMatch = text.match(/@addTagHelper\s+(.+)/);
          return addTagHelperMatch ? addTagHelperMatch[1].trim() : null;
        }
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
    if (tagNode) {
      return this.getNodeText(tagNode);
    }

    // Fallback: extract tag name from node text using regex
    const nodeText = this.getNodeText(node);
    const tagMatch = nodeText.match(/^<(\w+)/);
    if (tagMatch) {
      return tagMatch[1];
    }

    return 'div';
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
      const childText = this.getNodeText(child);
      if (['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'sealed', 'async'].includes(child.type) ||
          ['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'sealed', 'async'].includes(childText)) {
        modifiers.push(childText);
      }
    }
    return modifiers;
  }

  private extractMethodParameters(node: Parser.SyntaxNode): string | null {
    const paramList = node.children.find(c => c.type === 'parameter_list');
    return paramList ? this.getNodeText(paramList) : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    // Look for various return type patterns including generic types like Task<string>
    const returnType = node.children.find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'generic_name' ||
      c.type === 'qualified_name' ||
      c.type === 'nullable_type' ||
      c.type === 'array_type'
    );
    return returnType ? this.getNodeText(returnType) : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    // Look for the type node, which comes after modifiers but before the property name
    for (let i = 0; i < node.children.length; i++) {
      const child = node.children[i];

      // Skip attributes, modifiers
      if (child.type === 'attribute_list' ||
          ['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'sealed'].includes(child.type) ||
          ['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'sealed'].includes(this.getNodeText(child))) {
        continue;
      }

      // Look for type nodes
      if (child.type === 'predefined_type' ||
          child.type === 'nullable_type' ||
          child.type === 'array_type' ||
          child.type === 'generic_name' ||
          (child.type === 'identifier' && i < node.children.length - 2)) { // Not the last identifier (which would be property name)
        return this.getNodeText(child);
      }
    }

    return null;
  }

  private extractVariableType(node: Parser.SyntaxNode): string | null {
    const type = node.children.find(c => c.type === 'predefined_type' || c.type === 'identifier');
    return type ? this.getNodeText(type) : null;
  }

  private extractAttributes(node: Parser.SyntaxNode): string[] {
    const attributes: string[] = [];

    // Look for attribute_list nodes
    for (const child of node.children) {
      if (child.type === 'attribute_list') {
        const attributeText = this.getNodeText(child);
        attributes.push(attributeText);
      }
    }

    // Also check siblings for attributes that might be before the declaration
    if (node.parent) {
      const nodeIndex = node.parent.children.indexOf(node);
      for (let i = nodeIndex - 1; i >= 0; i--) {
        const sibling = node.parent.children[i];
        if (sibling.type === 'attribute_list') {
          const attributeText = this.getNodeText(sibling);
          attributes.unshift(attributeText); // Add to beginning to maintain order
        } else if (sibling.type !== 'newline' && sibling.type !== 'whitespace') {
          // Stop if we hit a non-whitespace, non-attribute node
          break;
        }
      }
    }

    return attributes;
  }

  private extractField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const variableDeclarator = node.children.find(c => c.type === 'variable_declarator');
    if (!variableDeclarator) return null;

    const nameNode = variableDeclarator.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownField';

    const modifiers = this.extractModifiers(node);
    const type = this.extractFieldType(node);
    const attributes = this.extractAttributes(node);

    let signature = ``;

    // Build signature in correct order: [attributes] [modifiers] [type] [name]
    if (attributes.length > 0) {
      signature += `${attributes.join(' ')} `;
    }

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (type) {
      signature += `${type} `;
    }

    signature += name;

    // Look for field initializer
    const equalsNode = variableDeclarator.children.find(c => c.type === '=');
    if (equalsNode) {
      const initializerIndex = variableDeclarator.children.indexOf(equalsNode) + 1;
      if (initializerIndex < variableDeclarator.children.length) {
        const initializer = this.getNodeText(variableDeclarator.children[initializerIndex]);
        signature += ` = ${initializer}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'field',
        modifiers,
        fieldType: type,
        attributes
      }
    });
  }

  private extractFieldType(node: Parser.SyntaxNode): string | null {
    const variableDeclaration = node.children.find(c => c.type === 'variable_declaration');
    if (!variableDeclaration) return null;

    const type = variableDeclaration.children.find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'generic_name' ||
      c.type === 'nullable_type' ||
      c.type === 'array_type'
    );
    return type ? this.getNodeText(type) : null;
  }

  private extractInvocation(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const memberAccess = node.children.find(c => c.type === 'member_access_expression');
    const identifier = node.children.find(c => c.type === 'identifier');

    let methodName = '';
    if (memberAccess) {
      methodName = this.getNodeText(memberAccess);
    } else if (identifier) {
      methodName = this.getNodeText(identifier);
    } else {
      return null;
    }

    const argumentList = node.children.find(c => c.type === 'argument_list');
    const args = argumentList ? this.getNodeText(argumentList) : '()';

    const signature = `${methodName}${args}`;

    return this.createSymbol(node, methodName, SymbolKind.Function, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'invocation',
        methodName,
        arguments: args
      }
    });
  }

  private extractElementAccess(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const expression = this.getNodeText(node);

    // Handle ViewData["key"] and ViewBag.property patterns
    if (expression.includes('ViewData[') || expression.includes('ViewBag.')) {
      let variableName = 'elementAccess';

      if (expression.includes('ViewData[')) {
        const match = expression.match(/ViewData\["([^"]+)"\]/);
        if (match) {
          variableName = `ViewData_${match[1]}`;
        }
      } else if (expression.includes('ViewBag.')) {
        const match = expression.match(/ViewBag\.(\w+)/);
        if (match) {
          variableName = `ViewBag_${match[1]}`;
        }
      }

      return this.createSymbol(node, variableName, SymbolKind.Variable, {
        signature: expression,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'element-access',
          expression,
          isViewData: expression.includes('ViewData'),
          isViewBag: expression.includes('ViewBag')
        }
      });
    }

    return null;
  }

  private extractLocalFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Local functions inside @code blocks (like OnInitializedAsync)
    // Handle explicit interface implementations like "void IDisposable.Dispose()"

    let name = 'unknownLocalFunction';


    // Look for explicit interface implementation pattern: InterfaceName.MethodName
    const explicitImplementation = node.children.find(c => c.type === 'explicit_interface_specifier');
    if (explicitImplementation) {
      // For explicit interface implementations, combine interface + method name
      const interfaceNode = explicitImplementation.children.find(c => c.type === 'identifier');
      const methodNode = node.children.find(c => c.type === 'identifier' && c !== interfaceNode);

      if (interfaceNode && methodNode) {
        const interfaceName = this.getNodeText(interfaceNode);
        const methodName = this.getNodeText(methodNode);
        name = `${interfaceName}.${methodName}`;
      }
    } else {
      // Regular method name extraction
      let nameNode: Parser.SyntaxNode | undefined;
      let foundReturnType = false;

      for (const child of node.children) {
        if (child.type === 'identifier') {
          if (foundReturnType) {
            // This is the method name, coming after the return type
            nameNode = child;
            break;
          } else {
            // Check if we've seen a type node (this could be the return type)
            const hasModifiers = node.children.some(c => ['modifier', 'async'].includes(c.type) || ['protected', 'override', 'async', 'public', 'private'].includes(this.getNodeText(c)));
            if (hasModifiers) {
              foundReturnType = true; // This identifier is likely the return type
            }
          }
        }
      }

      // Fallback: get the last identifier before parameter_list
      if (!nameNode) {
        const paramListIndex = node.children.findIndex(c => c.type === 'parameter_list');
        if (paramListIndex > 0) {
          for (let i = paramListIndex - 1; i >= 0; i--) {
            if (node.children[i].type === 'identifier') {
              nameNode = node.children[i];
              break;
            }
          }
        }
      }

      name = nameNode ? this.getNodeText(nameNode) : 'unknownLocalFunction';
    }

    const modifiers = this.extractModifiers(node);
    const parameters = this.extractMethodParameters(node);
    const returnType = this.extractReturnType(node);
    const attributes = this.extractAttributes(node);

    let signature = ``;

    // Build signature in correct order: [attributes] [modifiers] [returnType] [name] [parameters]
    if (attributes.length > 0) {
      signature += `${attributes.join(' ')} `;
    }

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (returnType) {
      signature += `${returnType} `;
    }

    signature += name;
    signature += parameters || '()';

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'local-function',
        modifiers,
        parameters,
        returnType,
        attributes
      }
    });
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
        case 'html_element':
        case 'element':
          // Check if this is a custom component
          this.extractElementRelationships(node, symbols, relationships);
          break;
        case 'identifier':
          // Check if this identifier looks like a component name (starts with uppercase)
          this.extractIdentifierComponentRelationships(node, symbols, relationships);
          break;
        case 'invocation_expression':
          this.extractInvocationRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractElementRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const tagName = this.extractHtmlTagName(node);

    // Check if this looks like a custom component (starts with uppercase)
    if (tagName && tagName[0] && tagName[0] === tagName[0].toUpperCase()) {
      let componentSymbol = symbols.find(s => s.name === tagName);

      if (!componentSymbol) {
        // Create a symbol for the external component
        componentSymbol = this.createSymbol(node, tagName, SymbolKind.Class, {
          signature: `<${tagName}>`,
          visibility: 'public',
          metadata: {
            type: 'external-component',
            isExternal: true
          }
        });
        symbols.push(componentSymbol);
      }

      relationships.push({
        fromSymbolId: `razor-page:${this.filePath}`,
        toSymbolId: componentSymbol.id,
        kind: RelationshipKind.Uses,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: componentSymbol.metadata?.isExternal ? 0.8 : 1.0,
        metadata: { componentName: tagName }
      });
    }
  }

  private extractIdentifierComponentRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const identifierName = this.getNodeText(node);

    // Check if this identifier looks like a component name (starts with uppercase and is not a property/variable)
    if (identifierName && identifierName[0] && identifierName[0] === identifierName[0].toUpperCase()) {
      // Additional check: make sure this is in a context that suggests it's a component usage
      // Look at parent context to see if this is in a Razor expression that could be a component
      const parentText = node.parent ? this.getNodeText(node.parent) : '';
      if (parentText.includes('<' + identifierName) || parentText.includes(identifierName + '>')) {
        let componentSymbol = symbols.find(s => s.name === identifierName);

        if (!componentSymbol) {
          // Create a symbol for the external component
          componentSymbol = this.createSymbol(node, identifierName, SymbolKind.Class, {
            signature: `<${identifierName}>`,
            visibility: 'public',
            metadata: {
              type: 'external-component',
              isExternal: true
            }
          });
          symbols.push(componentSymbol);
        }

        relationships.push({
          fromSymbolId: `razor-page:${this.filePath}`,
          toSymbolId: componentSymbol.id,
          kind: RelationshipKind.Uses,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: componentSymbol.metadata?.isExternal ? 0.8 : 1.0,
          metadata: { componentName: identifierName }
        });
      }
    }
  }

  private extractInvocationRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const methodCall = this.getNodeText(node);

    // Look for component invocations like Component.InvokeAsync("ComponentName")
    if (methodCall.includes('Component.InvokeAsync')) {
      const componentMatch = methodCall.match(/Component\.InvokeAsync\("([^"]+)"/);
      if (componentMatch) {
        const componentName = componentMatch[1];
        relationships.push({
          fromSymbolId: `razor-page:${this.filePath}`,
          toSymbolId: `component:${componentName}`,
          kind: RelationshipKind.Uses,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { componentName, invocationType: 'Component.InvokeAsync' }
        });
      }
    }
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


  private extractHtmlAttribute(node: Parser.SyntaxNode, parentId?: string, symbols?: Symbol[]): Symbol | null {
    // Get the complete attribute text from the parent element or broader context
    let attributeText = this.getNodeText(node);

    // If this is just the attribute name, try to get the parent's text for full attribute
    if (node.parent && (attributeText.startsWith('@') || node.type === 'razor_attribute_name')) {
      attributeText = this.getNodeText(node.parent);
    }

    // Handle data binding attributes like @bind-Value="Model.FirstName" or @bind-Value:event="oninput"
    if (attributeText.includes('@bind')) {
      // Extract the binding type and target, including custom event syntax
      const bindMatchWithEvent = attributeText.match(/@bind(-\w+)?:event="([^"]+)"/);
      const bindMatch = attributeText.match(/@bind(-\w+)?="([^"]+)"/);
      let bindingName = 'bind';
      let signature = attributeText;

      // Handle both value and event bindings when they exist in the same element
      if (bindMatch && bindMatchWithEvent && symbols) {
        // Create both symbols when both patterns exist
        const bindType = bindMatch[1] || ''; // e.g., "-Value"
        const bindTarget = bindMatch[2]; // e.g., "Model.FirstName"
        const eventType = bindMatchWithEvent[2]; // e.g., "oninput"

        // Create value binding symbol
        const valueSymbol = this.createSymbol(node, `bind${bindType}_${bindTarget.replace(/\./g, '_')}`, SymbolKind.Variable, {
          signature: `@bind${bindType}="${bindTarget}"`,
          visibility: 'public',
          parentId,
          metadata: {
            type: 'razor-data-binding',
            isDataBinding: true,
            bindingExpression: `@bind${bindType}="${bindTarget}"`
          }
        });

        // Create event binding symbol
        const eventSymbol = this.createSymbol(node, `bind${bindType}_event_${eventType}`, SymbolKind.Variable, {
          signature: `@bind${bindType}:event="${eventType}"`,
          visibility: 'public',
          parentId,
          metadata: {
            type: 'razor-event-binding',
            isEventBinding: true,
            bindingExpression: `@bind${bindType}:event="${eventType}"`
          }
        });

        // Add the additional symbol to the array
        symbols.push(eventSymbol);

        // Return the value symbol as primary
        return valueSymbol;
      }
      // Handle single patterns
      else if (bindMatchWithEvent) {
        // Handle @bind-Value:event="oninput" pattern
        const bindType = bindMatchWithEvent[1] || ''; // e.g., "-Value"
        const eventType = bindMatchWithEvent[2]; // e.g., "oninput"
        bindingName = `bind${bindType}_event_${eventType}`;
        signature = `@bind${bindType}:event="${eventType}"`;
      } else if (bindMatch) {
        // Handle @bind-Value="Model.FirstName" pattern
        const bindType = bindMatch[1] || ''; // e.g., "-Value"
        const bindTarget = bindMatch[2]; // e.g., "Model.FirstName"
        bindingName = `bind${bindType}_${bindTarget.replace(/\./g, '_')}`;
        signature = `@bind${bindType}="${bindTarget}"`;
      } else {
        // Fallback for simpler patterns
        const simpleMatch = attributeText.match(/@bind-(\w+)/);
        if (simpleMatch) {
          bindingName = `bind_${simpleMatch[1]}`;
        }
      }

      return this.createSymbol(node, bindingName, SymbolKind.Variable, {
        signature,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-binding',
          isDataBinding: true,
          bindingExpression: attributeText
        }
      });
    }

    // Handle event binding attributes like @onclick, @onchange
    if (attributeText.match(/@on\w+/)) {
      const signature = attributeText;
      const eventMatch = attributeText.match(/@(on\w+)/);
      const eventName = eventMatch ? eventMatch[1] : 'event';

      return this.createSymbol(node, eventName, SymbolKind.Function, {
        signature,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-event-binding',
          isEventBinding: true,
          eventExpression: attributeText
        }
      });
    }

    return null;
  }

  private extractRazorAttribute(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const attributeText = this.getNodeText(node);

    // Handle data binding attributes like @bind-Value="Model.FirstName"
    if (attributeText.includes('@bind')) {
      const signature = attributeText;
      let attributeName = 'binding';

      // Extract the binding target
      const bindMatch = attributeText.match(/@bind(-\w+)?="([^"]+)"/);
      if (bindMatch) {
        const bindType = bindMatch[1] || '';
        const bindTarget = bindMatch[2];
        attributeName = `bind${bindType}_${bindTarget.replace(/\./g, '_')}`;
      }

      return this.createSymbol(node, attributeName, SymbolKind.Variable, {
        signature,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-data-binding',
          isDataBinding: true,
          bindingExpression: attributeText
        }
      });
    }

    // Handle event binding attributes like @onclick, @onchange
    if (attributeText.match(/@on\w+/)) {
      const signature = attributeText;
      const eventMatch = attributeText.match(/@(on\w+)/);
      const eventName = eventMatch ? eventMatch[1] : 'event';

      return this.createSymbol(node, eventName, SymbolKind.Function, {
        signature,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'razor-event-binding',
          isEventBinding: true,
          eventExpression: attributeText
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
    } else {
      // Look at the full text to extract the value
      const fullText = this.getNodeText(node.parent || node);
      const match = fullText.match(/@\w+\s+(\S+)/);
      if (match) {
        directiveValue = match[1];
      }
    }

    const signature = directiveValue ? `${directiveName} ${directiveValue}` : directiveName;
    const symbolKind = this.getDirectiveSymbolKind(directiveType);

    return this.createSymbol(node, directiveName, symbolKind, {
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
      let inferredType = 'unknown';


      // Use actual type information from metadata
      if (symbol.metadata?.propertyType) {
        inferredType = symbol.metadata.propertyType;
      } else if (symbol.metadata?.fieldType) {
        inferredType = symbol.metadata.fieldType;
      } else if (symbol.metadata?.variableType) {
        inferredType = symbol.metadata.variableType;
      } else if (symbol.metadata?.returnType) {
        inferredType = symbol.metadata.returnType;
      } else if (symbol.signature) {
        // Try to extract type from signature - look for type before property/method name
        // Match patterns like: "[Parameter] public string? PropertyName" or "private bool FieldName"
        const typePatterns = [
          // For properties/fields: [attributes] [modifiers] TYPE NAME
          /(?:\[\w+.*?\]\s+)?(?:public|private|protected|internal|static)\s+(\w+(?:<[^>]+>)?(?:\?|\[\])?)\s+\w+/,
          // For methods: [modifiers] RETURNTYPE NAME(params)
          /(?:public|private|protected|internal|static|async)\s+(\w+(?:<[^>]+>)?)\s+\w+\s*\(/,
          // For variables: TYPE NAME = value
          /(\w+(?:<[^>]+>)?(?:\?|\[\])?)\s+\w+\s*=/,
          // Simple type extraction - get the type that comes before the symbol name
          new RegExp(`\\s+(\\w+(?:<[^>]+>)?(?:\\?|\\[\\])?)\\s+${symbol.name}\\b`)
        ];

        for (const pattern of typePatterns) {
          const match = symbol.signature.match(pattern);
          if (match && match[1] && match[1] !== symbol.name) {
            inferredType = match[1];
            break;
          }
        }
      }

      // Handle special cases
      if (symbol.metadata?.isDataBinding) {
        inferredType = 'bool';
      } else if (symbol.kind === SymbolKind.Method && symbol.signature?.includes('async Task')) {
        inferredType = 'Task';
      } else if (symbol.kind === SymbolKind.Method && symbol.signature?.includes('void')) {
        inferredType = 'void';
      }

      types.set(symbol.id, inferredType);
    }
    return types;
  }
}