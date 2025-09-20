import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class KotlinExtractor extends BaseExtractor {
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
        case 'object_declaration':
          symbol = this.extractObject(node, parentId);
          break;
        case 'function_declaration':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'enum_class_body':
          this.extractEnumMembers(node, symbols, parentId);
          break;
        case 'package_header':
          symbol = this.extractPackage(node, parentId);
          break;
        case 'import_header':
          symbol = this.extractImport(node, parentId);
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
      console.warn('Kotlin parsing failed:', error);
    }
    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownClass';

    // Check if this is actually an interface by looking for 'interface' child node
    const isInterface = node.children.some(c => c.type === 'interface');

    const modifiers = this.extractModifiers(node);
    const typeParams = this.extractTypeParameters(node);
    const superTypes = this.extractSuperTypes(node);

    let signature = isInterface ? `interface ${name}` : `class ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += typeParams;
    }

    if (superTypes) {
      signature += ` : ${superTypes}`;
    }

    const symbolKind = isInterface ? SymbolKind.Interface : this.determineClassKind(modifiers, node);

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'class',
        modifiers,
        typeParameters: typeParams,
        superTypes: superTypes
      }
    });
  }

  private extractInterface(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownInterface';

    const modifiers = this.extractModifiers(node);
    const typeParams = this.extractTypeParameters(node);
    const superTypes = this.extractSuperTypes(node);

    let signature = `interface ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += typeParams;
    }

    if (superTypes) {
      signature += ` : ${superTypes}`;
    }

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'interface',
        modifiers,
        typeParameters: typeParams
      }
    });
  }

  private extractObject(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownObject';

    const modifiers = this.extractModifiers(node);
    const superTypes = this.extractSuperTypes(node);

    let signature = `object ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (superTypes) {
      signature += ` : ${superTypes}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'object',
        modifiers
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownFunction';

    const modifiers = this.extractModifiers(node);
    const typeParams = this.extractTypeParameters(node);
    const parameters = this.extractParameters(node);
    const returnType = this.extractReturnType(node);

    let signature = `fun ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += typeParams;
    }

    signature += parameters || '()';

    if (returnType) {
      signature += `: ${returnType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'function',
        modifiers,
        parameters: parameters,
        returnType: returnType
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const type = this.extractPropertyType(node);
    const isVal = node.children.some(c => c.type === 'val');
    const isVar = node.children.some(c => c.type === 'var');

    let signature = `${isVal ? 'val' : isVar ? 'var' : 'val'} ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (type) {
      signature += `: ${type}`;
    }

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'property',
        modifiers,
        propertyType: type,
        isVal: isVal,
        isVar: isVar
      }
    });
  }

  private extractEnumMembers(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    for (const child of node.children) {
      if (child.type === 'enum_entry') {
        const nameNode = child.children.find(c => c.type === 'simple_identifier');
        if (nameNode) {
          const name = this.getNodeText(nameNode);
          const symbol = this.createSymbol(child, name, SymbolKind.EnumMember, {
            signature: name,
            visibility: 'public',
            parentId,
            metadata: {
              type: 'enum-member'
            }
          });
          symbols.push(symbol);
        }
      }
    }
  }

  private extractPackage(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownPackage';

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature: `package ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'package'
      }
    });
  }

  private extractImport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownImport';

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature: `import ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'import'
      }
    });
  }

  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];
    const modifiersList = node.children.find(c => c.type === 'modifiers');

    if (modifiersList) {
      for (const child of modifiersList.children) {
        if (['public', 'private', 'protected', 'internal', 'open', 'final', 'abstract', 'sealed', 'data', 'inline', 'suspend', 'operator', 'infix'].includes(child.type)) {
          modifiers.push(this.getNodeText(child));
        }
      }
    }

    return modifiers;
  }

  private extractTypeParameters(node: Parser.SyntaxNode): string | null {
    const typeParams = node.children.find(c => c.type === 'type_parameters');
    return typeParams ? this.getNodeText(typeParams) : null;
  }

  private extractSuperTypes(node: Parser.SyntaxNode): string | null {
    const delegation = node.children.find(c => c.type === 'delegation_specifiers');
    return delegation ? this.getNodeText(delegation) : null;
  }

  private extractParameters(node: Parser.SyntaxNode): string | null {
    const params = node.children.find(c => c.type === 'function_value_parameters');
    return params ? this.getNodeText(params) : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    const returnType = node.children.find(c => c.type === 'type');
    return returnType ? this.getNodeText(returnType) : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    const type = node.children.find(c => c.type === 'type');
    return type ? this.getNodeText(type) : null;
  }

  private determineClassKind(modifiers: string[], node: Parser.SyntaxNode): SymbolKind {
    if (modifiers.includes('enum')) return SymbolKind.Enum;
    if (modifiers.includes('data')) return SymbolKind.Class;
    if (modifiers.includes('sealed')) return SymbolKind.Class;
    return SymbolKind.Class;
  }

  private determineVisibility(modifiers: string[]): 'public' | 'private' | 'protected' {
    if (modifiers.includes('private')) return 'private';
    if (modifiers.includes('protected')) return 'protected';
    return 'public'; // Kotlin defaults to public
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'class_declaration':
        case 'object_declaration':
          this.extractInheritanceRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractInheritanceRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const classSymbol = this.findClassSymbol(node, symbols);
    if (!classSymbol) return;

    const delegation = node.children.find(c => c.type === 'delegation_specifiers');
    if (delegation) {
      // Parse inheritance relationships
      for (const delegationChild of delegation.children) {
        if (delegationChild.type === 'delegated_super_type') {
          const typeNode = delegationChild.children.find(c => c.type === 'type');
          if (typeNode) {
            const baseTypeName = this.getNodeText(typeNode);
            relationships.push({
              fromSymbolId: classSymbol.id,
              toSymbolId: `kotlin-type:${baseTypeName}`,
              kind: RelationshipKind.Extends,
              filePath: this.filePath,
              lineNumber: node.startPosition.row + 1,
              confidence: 1.0,
              metadata: { baseType: baseTypeName }
            });
          }
        }
      }
    }
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

  private findClassSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const className = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === className &&
      (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface) &&
      s.filePath === this.filePath
    ) || null;
  }
}