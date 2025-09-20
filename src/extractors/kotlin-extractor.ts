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
        case 'enum_declaration': // Handle enum classes
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
        case 'type_alias': // Correct node type for type aliases
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
    const constructorParams = this.extractPrimaryConstructorSignature(node);

    // Determine if this is an enum class
    const isEnum = this.determineClassKind(modifiers, node) === SymbolKind.Enum;

    // Check for fun interface by looking for direct 'fun' child
    const hasFunKeyword = node.children.some(c => this.getNodeText(c) === 'fun');

    let signature = isInterface ?
                     (hasFunKeyword ? `fun interface ${name}` : `interface ${name}`) :
                   isEnum ? `enum class ${name}` :
                   `class ${name}`;

    // For enum classes, don't include 'enum' in modifiers since it's already in the signature
    // For fun interfaces, don't include 'fun' in modifiers since it's already in the signature
    const finalModifiers = isEnum ? modifiers.filter(m => m !== 'enum') :
                          (hasFunKeyword ? modifiers.filter(m => m !== 'fun') : modifiers);

    if (finalModifiers.length > 0) {
      signature = `${finalModifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += typeParams;
    }

    // Add primary constructor parameters to signature if present
    if (constructorParams) {
      signature += constructorParams;
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
    const receiverType = this.extractReceiverType(node);
    const parameters = this.extractParameters(node);
    const returnType = this.extractReturnType(node);

    // Correct Kotlin signature order: modifiers + fun + typeParams + name
    let signature = 'fun';

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (typeParams) {
      signature += ` ${typeParams}`;
    }

    // Add receiver type for extension functions (e.g., String.functionName)
    if (receiverType) {
      signature += ` ${receiverType}.${name}`;
    } else {
      signature += ` ${name}`;
    }

    signature += parameters || '()';

    if (returnType) {
      signature += `: ${returnType}`;
    }

    // Check for where clause (sibling node)
    const whereClause = this.extractWhereClause(node);
    if (whereClause) {
      signature += ` ${whereClause}`;
    }

    // Check for expression body (= expression)
    const functionBody = node.children.find(c => c.type === 'function_body');
    if (functionBody && functionBody.text.startsWith('=')) {
      signature += ` ${functionBody.text}`;
    }

    // Determine symbol kind based on modifiers and context
    let symbolKind: SymbolKind;
    if (modifiers.includes('operator')) {
      symbolKind = SymbolKind.Operator;
    } else if (parentId) {
      symbolKind = SymbolKind.Method;
    } else {
      symbolKind = SymbolKind.Function;
    }

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

    // Add initializer value if present (especially for const val)
    const initializer = this.extractPropertyInitializer(node);
    if (initializer) {
      signature += ` = ${initializer}`;
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

          // Check for constructor parameters
          let signature = name;
          const valueArgs = child.children.find(c => c.type === 'value_arguments');
          if (valueArgs) {
            const args = this.getNodeText(valueArgs);
            signature += args;
          }

          const symbol = this.createSymbol(child, name, SymbolKind.EnumMember, {
            signature,
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

    // Find the aliased type (after =) - may consist of multiple nodes
    let aliasedType = '';
    const equalIndex = node.children.findIndex(c => this.getNodeText(c) === '=');
    if (equalIndex !== -1 && equalIndex + 1 < node.children.length) {
      // Concatenate all nodes after the = (e.g., "suspend" + "(T) -> Unit")
      const typeNodes = node.children.slice(equalIndex + 1);
      aliasedType = typeNodes.map(n => this.getNodeText(n)).join(' ');
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
      if (foundColon && (
        child.type === 'type' ||
        child.type === 'user_type' ||
        child.type === 'simple_identifier' ||
        child.type === 'function_type' ||
        child.type === 'nullable_type'
      )) {
        return this.getNodeText(child);
      }
    }
    return null;
  }

  private extractPropertyInitializer(node: Parser.SyntaxNode): string | null {
    // Look for assignment (=) followed by initializer expression
    const assignmentIndex = node.children.findIndex(c => this.getNodeText(c) === '=');
    if (assignmentIndex !== -1 && assignmentIndex + 1 < node.children.length) {
      const initializerNode = node.children[assignmentIndex + 1];
      return this.getNodeText(initializerNode).trim();
    }

    // Also check for property_initializer node type
    const initializerNode = node.children.find(c =>
      c.type === 'property_initializer' ||
      c.type === 'expression' ||
      c.type === 'literal'
    );
    if (initializerNode) {
      return this.getNodeText(initializerNode).trim();
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

  private extractPrimaryConstructorSignature(node: Parser.SyntaxNode): string | null {
    // Look for primary_constructor node
    const primaryConstructor = node.children.find(c => c.type === 'primary_constructor');
    if (!primaryConstructor) return null;

    // Extract class parameters
    const params: string[] = [];
    for (const child of primaryConstructor.children) {
      if (child.type === 'class_parameter') {
        const nameNode = child.children.find(c => c.type === 'simple_identifier');
        const name = nameNode ? this.getNodeText(nameNode) : 'unknownParam';

        // Get binding pattern (val/var)
        const bindingNode = child.children.find(c => c.type === 'binding_pattern_kind');
        const binding = bindingNode ? this.getNodeText(bindingNode) : '';

        // Get type
        const typeNode = child.children.find(c =>
          c.type === 'user_type' ||
          c.type === 'type' ||
          c.type === 'nullable_type' ||
          c.type === 'type_reference'
        );
        const type = typeNode ? this.getNodeText(typeNode) : '';

        // Get modifiers (like private)
        const modifiersNode = child.children.find(c => c.type === 'modifiers');
        const modifiers = modifiersNode ? this.getNodeText(modifiersNode) : '';

        // Build parameter signature
        let paramSig = '';
        if (modifiers) {
          paramSig += `${modifiers} `;
        }
        if (binding) {
          paramSig += `${binding} `;
        }
        paramSig += name;
        if (type) {
          paramSig += `: ${type}`;
        }

        params.push(paramSig);
      }
    }

    return params.length > 0 ? `(${params.join(', ')})` : null;
  }

  private extractWhereClause(node: Parser.SyntaxNode): string | null {
    // Where clauses are parsed as sibling infix_expression nodes
    if (!node.parent) return null;

    const siblings = node.parent.children;

    // Find current node by position comparison
    let currentIndex = -1;
    for (let i = 0; i < siblings.length; i++) {
      if (siblings[i].startPosition.row === node.startPosition.row &&
          siblings[i].startPosition.column === node.startPosition.column) {
        currentIndex = i;
        break;
      }
    }

    // Look for the immediate next sibling that's an infix_expression starting with "where"
    if (currentIndex !== -1 && currentIndex + 1 < siblings.length) {
      const nextSibling = siblings[currentIndex + 1];
      if (nextSibling.type === 'infix_expression' && nextSibling.text.trim().startsWith('where')) {
        // Extract just the where clause part (before the function body)
        const whereText = nextSibling.text.split('{')[0].trim();
        return whereText;
      }
    }

    return null;
  }

  private extractReceiverType(node: Parser.SyntaxNode): string | null {
    // Look for receiver_type node for extension functions
    const receiverTypeNode = node.children.find(c => c.type === 'receiver_type');
    return receiverTypeNode ? this.getNodeText(receiverTypeNode) : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    // Look for type in variable_declaration (interface properties)
    const varDecl = node.children.find(c => c.type === 'variable_declaration');
    if (varDecl) {
      const userType = varDecl.children.find(c =>
        c.type === 'user_type' ||
        c.type === 'type' ||
        c.type === 'nullable_type' ||
        c.type === 'type_reference'
      );
      if (userType) {
        return this.getNodeText(userType);
      }
    }

    // Look for direct type node (regular properties)
    const type = node.children.find(c =>
      c.type === 'type' ||
      c.type === 'user_type' ||
      c.type === 'nullable_type' ||
      c.type === 'type_reference'
    );
    return type ? this.getNodeText(type) : null;
  }

  private determineClassKind(modifiers: string[], node: Parser.SyntaxNode): SymbolKind {
    // Check if this is an enum declaration by node type
    if (node.type === 'enum_declaration') return SymbolKind.Enum;

    // Check for enum class by looking for 'enum' keyword in the node
    const hasEnumKeyword = node.children.some(c => this.getNodeText(c) === 'enum');
    if (hasEnumKeyword) return SymbolKind.Enum;

    // Check modifiers
    if (modifiers.includes('enum') || modifiers.includes('enum class')) return SymbolKind.Enum;
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
        case 'enum_declaration': // Include enum classes
        case 'object_declaration':
        case 'interface_declaration': // Include interfaces
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
          // Extract type nodes directly - handle both user_type and constructor_invocation
          const typeNode = delegation.children.find(c =>
            c.type === 'type' ||
            c.type === 'user_type' ||
            c.type === 'simple_identifier' ||
            c.type === 'constructor_invocation'
          );
          if (typeNode) {
            if (typeNode.type === 'constructor_invocation') {
              // For constructor invocations like Widget(), extract just the type name
              const userTypeNode = typeNode.children.find(c => c.type === 'user_type');
              if (userTypeNode) {
                baseTypeNames.push(this.getNodeText(userTypeNode));
              }
            } else {
              baseTypeNames.push(this.getNodeText(typeNode));
            }
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

        // Get type (handle various type node structures including nullable)
        const typeNode = child.children.find(c =>
          c.type === 'user_type' ||
          c.type === 'type' ||
          c.type === 'nullable_type' ||
          c.type === 'type_reference'
        );
        const type = typeNode ? this.getNodeText(typeNode) : '';

        // Get modifiers (like private)
        const modifiersNode = child.children.find(c => c.type === 'modifiers');
        const modifiers = modifiersNode ? this.getNodeText(modifiersNode) : '';

        // Get default value (handle various literal types and expressions)
        const defaultValue = child.children.find(c =>
          c.type === 'integer_literal' ||
          c.type === 'string_literal' ||
          c.type === 'boolean_literal' ||
          c.type === 'expression' ||
          c.type === 'call_expression'
        );
        const defaultVal = defaultValue ? ` = ${this.getNodeText(defaultValue)}` : '';

        // Alternative: look for assignment pattern (= value)
        if (!defaultVal) {
          const equalIndex = child.children.findIndex(c => this.getNodeText(c) === '=');
          if (equalIndex !== -1 && equalIndex + 1 < child.children.length) {
            const valueNode = child.children[equalIndex + 1];
            const defaultAssignment = ` = ${this.getNodeText(valueNode)}`;
            // Use this value instead
            const signature2 = `${binding} ${name}`;
            const finalSig = type ? `${signature2}: ${type}${defaultAssignment}` : `${signature2}${defaultAssignment}`;
            const finalSignature = modifiers ? `${modifiers} ${finalSig}` : finalSig;

            symbols.push(this.createSymbol(child, name, SymbolKind.Property, {
              signature: finalSignature,
              visibility: modifiers.includes('private') ? 'private' : 'public',
              parentId,
              metadata: {
                type: 'constructor-parameter',
                binding,
                propertyType: type
              }
            }));
            continue;
          }
        }

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