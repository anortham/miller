import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class PhpExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'interface_declaration':
          symbol = this.extractInterface(node, parentId);
          break;
        case 'trait_declaration':
          symbol = this.extractTrait(node, parentId);
          break;
        case 'enum_declaration':
          symbol = this.extractEnum(node, parentId);
          break;
        case 'function_definition':
        case 'method_declaration':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'const_declaration':
          symbol = this.extractConstant(node, parentId);
          break;
        case 'namespace_definition':
          symbol = this.extractNamespace(node, parentId);
          break;
        case 'use_declaration':
        case 'namespace_use_declaration':
          symbol = this.extractUse(node, parentId);
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
      console.warn('PHP parsing failed:', error);
    }
    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownClass';

    const modifiers = this.extractModifiers(node);
    const extendsNode = node.children.find(c => c.type === 'base_clause');
    const implementsNode = node.children.find(c => c.type === 'class_interface_clause');

    let signature = `class ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (extendsNode) {
      const baseClass = this.getNodeText(extendsNode).replace('extends', '').trim();
      signature += ` extends ${baseClass}`;
    }

    if (implementsNode) {
      const interfaces = this.getNodeText(implementsNode).replace('implements', '').trim();
      signature += ` implements ${interfaces}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'class',
        modifiers,
        extends: extendsNode ? this.getNodeText(extendsNode) : null,
        implements: implementsNode ? this.getNodeText(implementsNode) : null
      }
    });
  }

  private extractInterface(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownInterface';

    const extendsNode = node.children.find(c => c.type === 'base_clause');
    let signature = `interface ${name}`;

    if (extendsNode) {
      const baseInterfaces = this.getNodeText(extendsNode).replace('extends', '').trim();
      signature += ` extends ${baseInterfaces}`;
    }

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'interface',
        extends: extendsNode ? this.getNodeText(extendsNode) : null
      }
    });
  }

  private extractTrait(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownTrait';

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature: `trait ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'trait'
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownFunction';

    const modifiers = this.extractModifiers(node);
    const parametersNode = node.children.find(c => c.type === 'formal_parameters');

    // PHP return type comes after : as primitive_type node
    const colonIndex = node.children.findIndex(c => c.type === ':');
    let returnTypeNode = null;
    if (colonIndex >= 0 && colonIndex + 1 < node.children.length) {
      const nextNode = node.children[colonIndex + 1];
      if (nextNode.type === 'primitive_type' || nextNode.type === 'named_type') {
        returnTypeNode = nextNode;
      }
    }

    // Check for reference modifier (&)
    const referenceModifier = node.children.find(c => c.type === 'reference_modifier');
    const refPrefix = referenceModifier ? '&' : '';

    let signature = `function ${refPrefix}${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (parametersNode) {
      signature += this.getNodeText(parametersNode);
    } else {
      signature += '()';
    }

    if (returnTypeNode) {
      signature += `: ${this.getNodeText(returnTypeNode)}`;
    }

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'function',
        modifiers,
        parameters: parametersNode ? this.getNodeText(parametersNode) : '()',
        returnType: returnTypeNode ? this.getNodeText(returnTypeNode) : null
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract property name from property_element
    const propertyElement = node.children.find(c => c.type === 'property_element');
    if (!propertyElement) return null;

    const nameNode = propertyElement.children.find(c => c.type === 'variable_name');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const typeNode = node.children.find(c => c.type === 'type' || c.type === 'primitive_type');

    let signature = `${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeNode) {
      signature = `${this.getNodeText(typeNode)} ${signature}`;
    }

    return this.createSymbol(node, name.replace('$', ''), SymbolKind.Property, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'property',
        modifiers,
        propertyType: typeNode ? this.getNodeText(typeNode) : null
      }
    });
  }

  private extractConstant(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UNKNOWN_CONST';

    const valueNode = node.children.find(c => c.type === 'expression');
    let signature = `const ${name}`;

    if (valueNode) {
      const value = this.getNodeText(valueNode);
      signature += ` = ${value}`;
    }

    return this.createSymbol(node, name, SymbolKind.Constant, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'constant',
        value: valueNode ? this.getNodeText(valueNode) : null
      }
    });
  }

  private extractNamespace(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'namespace_name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownNamespace';

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature: `namespace ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'namespace'
      }
    });
  }

  private extractUse(node: Parser.SyntaxNode, parentId?: string): Symbol {
    let nameNode: Parser.SyntaxNode | undefined;
    let name: string;

    if (node.type === 'namespace_use_declaration') {
      // Handle new namespace_use_declaration format
      const useClause = node.children.find(c => c.type === 'namespace_use_clause');
      if (useClause) {
        nameNode = useClause.children.find(c => c.type === 'qualified_name');
        name = nameNode ? this.getNodeText(nameNode) : 'UnknownImport';
      } else {
        name = 'UnknownImport';
      }
    } else {
      // Handle legacy use_declaration format
      nameNode = node.children.find(c => c.type === 'namespace_name' || c.type === 'qualified_name');
      name = nameNode ? this.getNodeText(nameNode) : 'UnknownImport';
    }

    const aliasNode = node.children.find(c => c.type === 'namespace_aliasing_clause');
    let signature = `use ${name}`;

    if (aliasNode) {
      signature += ` ${this.getNodeText(aliasNode)}`;
    }

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'use',
        alias: aliasNode ? this.getNodeText(aliasNode) : null
      }
    });
  }

  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];

    for (const child of node.children) {
      if (child.type === 'visibility_modifier') {
        // Handle visibility_modifier node
        modifiers.push(this.getNodeText(child));
      } else if (child.type === 'abstract_modifier') {
        // Handle abstract_modifier node
        modifiers.push('abstract');
      } else if (child.type === 'static_modifier') {
        // Handle static_modifier node
        modifiers.push('static');
      } else if (child.type === 'final_modifier') {
        // Handle final_modifier node
        modifiers.push('final');
      } else if (child.type === 'readonly_modifier') {
        // Handle readonly_modifier node
        modifiers.push('readonly');
      } else if (['public', 'private', 'protected', 'static', 'abstract', 'final', 'readonly'].includes(child.type)) {
        // Handle direct modifier nodes (fallback)
        modifiers.push(this.getNodeText(child));
      }
    }

    return modifiers;
  }

  private determineVisibility(modifiers: string[]): 'public' | 'private' | 'protected' {
    if (modifiers.includes('private')) return 'private';
    if (modifiers.includes('protected')) return 'protected';
    return 'public'; // PHP defaults to public
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'class_declaration':
          this.extractClassRelationships(node, symbols, relationships);
          break;
        case 'interface_declaration':
          this.extractInterfaceRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractClassRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const classSymbol = this.findClassSymbol(node, symbols);
    if (!classSymbol) return;

    // Inheritance relationships
    const extendsNode = node.children.find(c => c.type === 'base_clause');
    if (extendsNode) {
      const baseClassName = this.getNodeText(extendsNode).replace('extends', '').trim();
      relationships.push({
        fromSymbolId: classSymbol.id,
        toSymbolId: `php-class:${baseClassName}`,
        kind: RelationshipKind.Extends,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { baseClass: baseClassName }
      });
    }

    // Implementation relationships
    const implementsNode = node.children.find(c => c.type === 'class_interface_clause');
    if (implementsNode) {
      const interfaceNames = this.getNodeText(implementsNode)
        .replace('implements', '')
        .split(',')
        .map(name => name.trim());

      for (const interfaceName of interfaceNames) {
        // Find the actual interface symbol instead of using synthetic ID
        const interfaceSymbol = symbols.find(s =>
          s.name === interfaceName &&
          s.kind === SymbolKind.Interface &&
          s.filePath === this.filePath
        );

        relationships.push({
          fromSymbolId: classSymbol.id,
          toSymbolId: interfaceSymbol?.id || `php-interface:${interfaceName}`,
          kind: RelationshipKind.Implements,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: interfaceSymbol ? 1.0 : 0.8,
          metadata: { interface: interfaceName }
        });
      }
    }
  }

  private extractInterfaceRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const interfaceSymbol = this.findInterfaceSymbol(node, symbols);
    if (!interfaceSymbol) return;

    // Interface inheritance
    const extendsNode = node.children.find(c => c.type === 'base_clause');
    if (extendsNode) {
      const baseInterfaceNames = this.getNodeText(extendsNode)
        .replace('extends', '')
        .split(',')
        .map(name => name.trim());

      for (const baseInterfaceName of baseInterfaceNames) {
        relationships.push({
          fromSymbolId: interfaceSymbol.id,
          toSymbolId: `php-interface:${baseInterfaceName}`,
          kind: RelationshipKind.Extends,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { baseInterface: baseInterfaceName }
        });
      }
    }
  }

  private findClassSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'name');
    const className = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === className &&
      s.kind === SymbolKind.Class &&
      s.filePath === this.filePath
    ) || null;
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

  private findInterfaceSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'name');
    const interfaceName = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === interfaceName &&
      s.kind === SymbolKind.Interface &&
      s.filePath === this.filePath
    ) || null;
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'name');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownEnum';

    // Check for backing type (e.g., enum Status: string)
    const colonIndex = node.children.findIndex(c => c.type === ':');
    let backingType = null;
    if (colonIndex >= 0 && colonIndex + 1 < node.children.length) {
      const typeNode = node.children[colonIndex + 1];
      if (typeNode.type === 'primitive_type') {
        backingType = this.getNodeText(typeNode);
      }
    }

    // Check for implements clause (e.g., implements JsonSerializable)
    const implementsNode = node.children.find(c => c.type === 'class_interface_clause');
    let implementsClause = null;
    if (implementsNode) {
      implementsClause = this.getNodeText(implementsNode).replace('implements', '').trim();
    }

    let signature = `enum ${name}`;
    if (backingType) {
      signature += `: ${backingType}`;
    }
    if (implementsClause) {
      signature += ` implements ${implementsClause}`;
    }

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'enum',
        backingType
      }
    });
  }
}