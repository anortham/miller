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
        case 'companion_object':
          symbol = this.extractCompanionObject(node, parentId);
          break;
        case 'function_declaration':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'property_declaration':
        case 'property_signature': // Interface properties
          symbol = this.extractProperty(node, parentId);
          break;
        case 'enum_class_body':
          this.extractEnumMembers(node, symbols, parentId);
          break;
        case 'primary_constructor':
          this.extractConstructorParameters(node, symbols, parentId);
          break;
        case 'package_header':
          symbol = this.extractPackage(node, parentId);
          break;
        case 'import_header':
          symbol = this.extractImport(node, parentId);
          break;
        case 'typealias_declaration':
          symbol = this.extractTypeAlias(node, parentId);
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

  private extractCompanionObject(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Companion objects always have the name "Companion"
    const name = 'Companion';

    let signature = 'companion object';

    // Check if companion object has a custom name
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    if (nameNode) {
      const customName = this.getNodeText(nameNode);
      signature += ` ${customName}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'companion-object'
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

    // Check for expression body (= expression)
    const functionBody = node.children.find(c => c.type === 'function_body');
    if (functionBody && functionBody.text.startsWith('=')) {
      signature += ` ${functionBody.text}`;
    }

    // Functions inside classes/interfaces are methods
    const symbolKind = parentId ? SymbolKind.Method : SymbolKind.Function;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: parentId ? 'method' : 'function',
        modifiers,
        parameters: parameters,
        returnType: returnType
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Look for name in variable_declaration (interface properties)
    let nameNode = node.children.find(c => c.type === 'simple_identifier');
    if (!nameNode) {
      const varDecl = node.children.find(c => c.type === 'variable_declaration');
      if (varDecl) {
        nameNode = varDecl.children.find(c => c.type === 'simple_identifier');
      }
    }
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const type = this.extractPropertyType(node);
    // Check for val/var in binding_pattern_kind for interface properties
    let isVal = node.children.some(c => c.type === 'val');
    let isVar = node.children.some(c => c.type === 'var');

    if (!isVal && !isVar) {
      const bindingPattern = node.children.find(c => c.type === 'binding_pattern_kind');
      if (bindingPattern) {
        isVal = bindingPattern.children.some(c => c.type === 'val');
        isVar = bindingPattern.children.some(c => c.type === 'var');
      }
    }

    let signature = `${isVal ? 'val' : isVar ? 'var' : 'val'} ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (type) {
      signature += `: ${type}`;
    }

    // Check for property delegation (by lazy, by Delegates.notNull(), etc.)
    const delegation = this.extractPropertyDelegation(node);
    if (delegation) {
      signature += ` ${delegation}`;
    }

    // Determine symbol kind - const val should be Constant
    const isConst = modifiers.includes('const');
    const symbolKind = isConst && isVal ? SymbolKind.Constant : SymbolKind.Property;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: isConst ? 'constant' : 'property',
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

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownTypeAlias';

    const modifiers = this.extractModifiers(node);
    const typeParams = this.extractTypeParameters(node);

    // Find the aliased type (after =)
    let aliasedType = '';
    const equalIndex = node.children.findIndex(c => this.getNodeText(c) === '=');
    if (equalIndex !== -1 && equalIndex + 1 < node.children.length) {
      const typeNode = node.children[equalIndex + 1];
      aliasedType = this.getNodeText(typeNode);
    }

    let signature = `typealias ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += typeParams;
    }

    if (aliasedType) {
      signature += ` = ${aliasedType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'typealias',
        modifiers,
        aliasedType: aliasedType
      }
    });
  }

  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];
    const modifiersList = node.children.find(c => c.type === 'modifiers');

    if (modifiersList) {
      for (const child of modifiersList.children) {
        // Handle modifier nodes by extracting their text content
        if (child.type === 'class_modifier' ||
            child.type === 'function_modifier' ||
            child.type === 'property_modifier' ||
            child.type === 'visibility_modifier' ||
            child.type === 'inheritance_modifier' ||
            child.type === 'member_modifier') {
          modifiers.push(this.getNodeText(child));
        }
        // Handle annotation nodes
        else if (child.type === 'annotation') {
          modifiers.push(this.getNodeText(child));
        }
        // Fallback: check for direct modifier keywords (backward compatibility)
        else if (['public', 'private', 'protected', 'internal', 'open', 'final', 'abstract', 'sealed', 'data', 'inline', 'suspend', 'operator', 'infix', 'annotation'].includes(child.type)) {
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
    const superTypes: string[] = [];

    // Look for delegation_specifiers container first (wrapped case)
    const delegationContainer = node.children.find(c => c.type === 'delegation_specifiers');
    if (delegationContainer) {
      for (const child of delegationContainer.children) {
        if (child.type === 'delegated_super_type') {
          const typeNode = child.children.find(c => c.type === 'type' || c.type === 'user_type' || c.type === 'simple_identifier');
          if (typeNode) {
            superTypes.push(this.getNodeText(typeNode));
          }
        } else if (child.type === 'type' || child.type === 'user_type' || child.type === 'simple_identifier') {
          superTypes.push(this.getNodeText(child));
        }
      }
    } else {
      // Look for individual delegation_specifier nodes (multiple at same level)
      const delegationSpecifiers = node.children.filter(c => c.type === 'delegation_specifier');
      for (const delegation of delegationSpecifiers) {
        superTypes.push(this.getNodeText(delegation));
      }
    }

    return superTypes.length > 0 ? superTypes.join(', ') : null;
  }

  private extractParameters(node: Parser.SyntaxNode): string | null {
    const params = node.children.find(c => c.type === 'function_value_parameters');
    return params ? this.getNodeText(params) : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    // Look for return type after the colon in function declarations
    let foundColon = false;
    for (const child of node.children) {
      if (child.type === ':') {
        foundColon = true;
        continue;
      }
      if (foundColon && (child.type === 'type' || child.type === 'user_type' || child.type === 'simple_identifier')) {
        return this.getNodeText(child);
      }
    }
    return null;
  }

  private extractPropertyDelegation(node: Parser.SyntaxNode): string | null {
    // Look for property_delegate or 'by' keyword
    const byIndex = node.children.findIndex(c => this.getNodeText(c) === 'by');
    if (byIndex !== -1 && byIndex + 1 < node.children.length) {
      const delegateNode = node.children[byIndex + 1];
      return `by ${this.getNodeText(delegateNode)}`;
    }

    // Also check for property_delegate node type
    const delegateNode = node.children.find(c => c.type === 'property_delegate');
    if (delegateNode) {
      return this.getNodeText(delegateNode);
    }

    return null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    // Look for type in variable_declaration (interface properties)
    const varDecl = node.children.find(c => c.type === 'variable_declaration');
    if (varDecl) {
      const userType = varDecl.children.find(c => c.type === 'user_type' || c.type === 'type');
      if (userType) {
        return this.getNodeText(userType);
      }
    }

    // Look for direct type node (regular properties)
    const type = node.children.find(c => c.type === 'type' || c.type === 'user_type');
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

    // Look for delegation_specifiers container first (wrapped case)
    const delegationContainer = node.children.find(c => c.type === 'delegation_specifiers');
    const baseTypeNames: string[] = [];

    if (delegationContainer) {
      for (const child of delegationContainer.children) {
        if (child.type === 'delegated_super_type') {
          const typeNode = child.children.find(c => c.type === 'type' || c.type === 'user_type' || c.type === 'simple_identifier');
          if (typeNode) {
            baseTypeNames.push(this.getNodeText(typeNode));
          }
        } else if (child.type === 'type' || child.type === 'user_type' || child.type === 'simple_identifier') {
          baseTypeNames.push(this.getNodeText(child));
        }
      }
    } else {
      // Look for individual delegation_specifier nodes (multiple at same level)
      const delegationSpecifiers = node.children.filter(c => c.type === 'delegation_specifier');
      for (const delegation of delegationSpecifiers) {
        // Extract just the type name from the delegation (remove "by delegate" part)
        const explicitDelegation = delegation.children.find(c => c.type === 'explicit_delegation');
        if (explicitDelegation) {
          const typeText = this.getNodeText(explicitDelegation);
          const typeName = typeText.split(' by ')[0]; // Get part before "by"
          baseTypeNames.push(typeName);
        } else {
          // Fallback - extract type nodes directly
          const typeNode = delegation.children.find(c => c.type === 'type' || c.type === 'user_type' || c.type === 'simple_identifier');
          if (typeNode) {
            baseTypeNames.push(this.getNodeText(typeNode));
          }
        }
      }
    }

    // Create relationships for each base type
    for (const baseTypeName of baseTypeNames) {
      // Find the actual base type symbol
      const baseTypeSymbol = symbols.find(s =>
        s.name === baseTypeName &&
        (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface || s.kind === SymbolKind.Struct)
      );

      if (baseTypeSymbol) {
        // Determine relationship kind: classes extend, interfaces implement
        const relationshipKind = baseTypeSymbol.kind === SymbolKind.Interface
          ? RelationshipKind.Implements
          : RelationshipKind.Extends;

        relationships.push({
          fromSymbolId: classSymbol.id,
          toSymbolId: baseTypeSymbol.id,
          kind: relationshipKind,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { baseType: baseTypeName }
        });
      }
    }
  }

  private extractConstructorParameters(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    // Extract class_parameter nodes as properties
    for (const child of node.children) {
      if (child.type === 'class_parameter') {
        const nameNode = child.children.find(c => c.type === 'simple_identifier');
        const name = nameNode ? this.getNodeText(nameNode) : 'unknownParam';

        // Get binding pattern (val/var)
        const bindingNode = child.children.find(c => c.type === 'binding_pattern_kind');
        const binding = bindingNode ? this.getNodeText(bindingNode) : 'val';

        // Get type
        const typeNode = child.children.find(c => c.type === 'user_type' || c.type === 'type');
        const type = typeNode ? this.getNodeText(typeNode) : '';

        // Get modifiers (like private)
        const modifiersNode = child.children.find(c => c.type === 'modifiers');
        const modifiers = modifiersNode ? this.getNodeText(modifiersNode) : '';

        // Get default value
        const defaultValue = child.children.find(c => c.type === 'integer_literal' || c.type === 'string_literal');
        const defaultVal = defaultValue ? ` = ${this.getNodeText(defaultValue)}` : '';

        // Build signature
        let signature = `${binding} ${name}`;
        if (type) {
          signature += `: ${type}`;
        }
        if (defaultVal) {
          signature += defaultVal;
        }

        // Add modifiers to signature if present
        if (modifiers) {
          signature = `${modifiers} ${signature}`;
        }

        // Determine visibility
        const visibility = modifiers.includes('private') ? 'private' :
                          modifiers.includes('protected') ? 'protected' : 'public';

        const propertySymbol = this.createSymbol(child, name, SymbolKind.Property, {
          signature,
          visibility,
          parentId,
          metadata: {
            type: 'property',
            binding,
            dataType: type,
            hasDefaultValue: !!defaultVal
          }
        });

        symbols.push(propertySymbol);
      }
    }
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.returnType) {
        types.set(symbol.id, symbol.metadata.returnType);
      } else if (symbol.metadata?.propertyType) {
        types.set(symbol.id, symbol.metadata.propertyType);
      } else if (symbol.metadata?.dataType) {
        types.set(symbol.id, symbol.metadata.dataType);
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